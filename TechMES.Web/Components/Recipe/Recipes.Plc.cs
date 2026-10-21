using System.Net;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

/// <summary>Онлайн-просмотр отделён от редактора и сохранённых значений рецепта.</summary>
public partial class Recipes
{
    // Пауза начинается после завершения предыдущего чтения, запросы не накапливаются.
    private static readonly TimeSpan PlcRefreshInterval = TimeSpan.FromSeconds(10);
    private CancellationTokenSource? _plcStop;
    private Task _plcTask = Task.CompletedTask;
    private bool _plcEnabled;
    private bool _plcLoading;
    private string? _plcError;
    private DateTimeOffset? _plcReadAtUtc;
    private Dictionary<(int StageId, int ColumnId), RecipePlcValueDto> _plcValues = [];

    private bool CanReadPlc => !EditorMode && !_disposed && _snapshot is not null && SelectedPage?.RecipeId == _snapshot.Recipe.Id;
    private string PlcStatus => _plcError ?? (_plcReadAtUtc.HasValue ? $"PLC: {_plcValues.Values.Count(item => item.Value.HasValue)}/{_plcValues.Count} · {_plcReadAtUtc.Value.ToLocalTime():HH:mm:ss}" : "Reading PLC values...");

    /// <summary>Переключение источника не меняет черновик и ничего не сохраняет.</summary>
    private void ChangePlcMode(bool enabled)
    {
        StopPlcReading();
        _plcEnabled = enabled && CanReadPlc;
        StartPlcReading();
    }

    /// <summary>Запускает один цикл для конкретного Unit и конкретной структуры рецепта.</summary>
    private void StartPlcReading()
    {
        if (!_plcEnabled || !CanReadPlc)
        {
            _plcEnabled = false;
            return;
        }

        var previous = _plcTask;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

        _plcStop = source;
        _plcTask = PollPlcAsync(previous, source, _snapshot!.Recipe.Id, SelectedPage!.Id, RecipePlcLayout.GetStamp(_snapshot));
    }

    /// <summary>Отменяет чтение до смены рецепта и сразу убирает данные прежнего Unit.</summary>
    private void StopPlcReading()
    {
        _plcStop?.Cancel();
        _plcStop = null;
        _plcValues = [];
        _plcReadAtUtc = null;
        _plcError = null;
        _plcLoading = false;
    }

    /// <summary>Результаты отменённого запроса никогда не попадают в новый рецепт.</summary>
    private async Task PollPlcAsync(Task previous, CancellationTokenSource source, int recipeId, int pageId, string layoutStamp)
    {
        var ct = source.Token;

        try
        {
            await previous;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                await InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _disposed) return;

                    _plcLoading = true;
                    StateHasChanged();
                });

                var stop = false;

                try
                {
                    var result = await RecipeApi.ReadPlcAsync(recipeId, pageId, layoutStamp, ct);
                    ct.ThrowIfCancellationRequested();

                    if (result.RecipeId != recipeId || result.PageId != pageId || result.LayoutStamp != layoutStamp)
                        throw new InvalidOperationException("PLC response does not match the selected recipe.");

                    var values = result.Values.ToDictionary(item => (item.StageId, item.ColumnId));

                    await InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _disposed) return;

                        _plcValues = values;
                        _plcReadAtUtc = result.ReadAtUtc;
                        _plcError = values.Values.Any(item => item.Error is not null) ? "Some PLC values are unavailable. Hover over — for details." : null;
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    stop = ex is HttpRequestException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.BadRequest };

                    Logger.LogWarning(ex, "Recipe PLC read failed. Recipe={RecipeId}, Unit={PageId}.", recipeId, pageId);

                    await InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _disposed) return;

                        _plcValues = [];
                        _plcReadAtUtc = null;
                        _plcError = ex.Message;
                    });
                }

                await InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _disposed) return;

                    _plcLoading = false;
                    StateHasChanged();
                });

                if (stop) return;

                await Task.Delay(PlcRefreshInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // При смене рецепта, выключении PLC и закрытии страницы опрос завершается.
        }
        catch (Exception ex)
        {
            // Ошибка уже закрытого Blazor renderer не должна оставлять необработанную Task.
            Logger.LogWarning(ex, "Recipe PLC polling stopped. Recipe={RecipeId}, Unit={PageId}.", recipeId, pageId);
        }
        finally
        {
            if (ReferenceEquals(_plcStop, source)) _plcStop = null;

            source.Dispose();
        }
    }

    private RecipePlcValueDto? FindPlcValue(RecipeStageDto stage, RecipeColumnDto column) => _plcValues.GetValueOrDefault((stage.Id, column.Id));

    private string RecipeValueTitle(RecipePlcValueDto? item) => !_plcEnabled ? EditorMode ? "Edit recipe value" : "Saved recipe value" : item is null ? _plcError ?? "Waiting for PLC value." : $"{item.TagName}: {item.Error ?? "Read from PLC"}";
}