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

    private bool CanReadPlc => !EditorMode && !_disposed && _snapshot is not null && SelectedPage is not null;

    private int PlcExpectedValueCount => Stages.Sum(stage => stage.Parameters.Count);
    private int PlcReadValueCount => Stages.Sum(stage => Columns.Count(column => stage.Parameters.ContainsKey(column.Id) && IsReadablePlcValue(FindPlcValue(stage, column), column.Kind)));
    private bool HasPlcReadWarning => _plcEnabled && (_plcError is not null || (_plcReadAtUtc.HasValue && PlcReadValueCount < PlcExpectedValueCount));

    private string PlcStatus
    {
        get
        {
            if (!CanReadPlc) return _loading ? "Loading recipe..." : "Select a Unit and a recipe.";

            var status = _plcReadAtUtc.HasValue ? $"PLC: {PlcReadValueCount}/{PlcExpectedValueCount} · {_plcReadAtUtc.Value.ToLocalTime():HH:mm:ss}" : "Reading PLC values...";

            return _plcError is null ? status : _plcReadAtUtc.HasValue ? $"{status} · {_plcError}" : _plcError;
        }
    }

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
        // Выбор другого рецепта не меняет включённый пользователем режим PLC.
        if (!_plcEnabled || !CanReadPlc || _plcStop is not null) return;

        var previous = _plcTask;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

        _plcStop = source;
        _plcTask = PollPlcAsync(previous, source, _snapshot!.Recipe.Id, SelectedPage!.Id, RecipePlcLayout.GetStamp(_snapshot));
    }

    /// <summary>Отменяет опрос; при смене рецепта также очищает предыдущий снимок.</summary>
    private void StopPlcReading(bool clearValues = true)
    {
        _plcStop?.Cancel();
        _plcStop = null;
        _plcLoading = false;

        // На время диалога сохраняем последний снимок, чтобы таблица не очищалась.
        if (!clearValues) return;

        _plcValues = [];
        _plcReadAtUtc = null;
        _plcError = null;
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
                        _plcError = PlcReadValueCount < PlcExpectedValueCount ? "Some PLC values are unavailable. Hover over the warning icon for details." : null;
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

    /// <summary>До первого ответа ячейка остаётся пустой; некорректный полученный ответ отображается предупреждением.</summary>
    private string? RecipeValueError(RecipeStageDto stage, RecipeColumnDto column)
    {
        if (!stage.Parameters.TryGetValue(column.Id, out var parameter)) return null;
        if (!_plcEnabled) return IsValidRecipeValue(parameter.Value, column.Kind) ? null : "Invalid saved recipe value.";

        var item = FindPlcValue(stage, column);

        if (item is null)
            return _plcReadAtUtc.HasValue ? "PLC did not return this parameter." : _plcError;

        if (item.Error is not null)
            return string.IsNullOrWhiteSpace(item.Error) ? "PLC value is unavailable." : item.Error;

        if (!item.Value.HasValue)
            return "PLC did not return a value for this parameter.";

        return IsValidRecipeValue(item.Value.Value, column.Kind) ? null : "PLC returned an invalid numeric or Boolean value.";
    }

    private static bool IsValidRecipeValue(double value, RecipeParameterKind kind) => double.IsFinite(value) && (kind == RecipeParameterKind.InputField || (kind == RecipeParameterKind.CheckBox && value is 0 or 1));

    private static bool IsReadablePlcValue(RecipePlcValueDto? item, RecipeParameterKind kind) => item is { Error: null, Value: not null } && IsValidRecipeValue(item.Value.Value, kind);

    private string RecipeValueTitle(RecipePlcValueDto? item, string? error = null) => !_plcEnabled ? error ?? (EditorMode ? "Edit recipe value" : "Saved recipe value") : item is null ? error ?? _plcError ?? "Waiting for PLC value." : $"{item.TagName}: {error ?? item.Error ?? "Read from PLC"}";
}