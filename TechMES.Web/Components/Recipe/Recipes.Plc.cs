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
    private int? _plcCurrentStage;
    private string? _plcCurrentStageError;
    private DateTimeOffset? _plcReadAtUtc;
    private Dictionary<(int StageId, int ColumnId), RecipePlcValueDto> _plcValues = [];

    private bool CanReadPlc => !EditorMode && !_disposed && _snapshot is not null && SelectedPage is not null;

    private int PlcExpectedValueCount => Stages.Sum(stage => stage.Parameters.Count);
    private int PlcReadValueCount => Stages.Sum(stage => Columns.Count(column => stage.Parameters.ContainsKey(column.Id) && IsReadablePlcValue(FindPlcValue(stage, column), column.Kind)));
    private bool HasPlcReadWarning => _plcEnabled && (_plcError is not null || _plcCurrentStageError is not null || (_plcReadAtUtc.HasValue && PlcReadValueCount < PlcExpectedValueCount));

    private bool IsCurrentPlcStage(RecipeStageDto stage) => !EditorMode && _plcEnabled && IsSelectedRecipeCurrent && _plcReadAtUtc.HasValue && _plcCurrentStageError is null && _plcCurrentStage is > 0 && stage.Number == _plcCurrentStage.Value;

    private string PlcStatus
    {
        get
        {
            if (!CanReadPlc) return _loading ? "Loading recipe..." : "Select a Unit and a recipe.";

            if (!_plcReadAtUtc.HasValue) return _plcError ?? "Reading PLC values...";

            var status = $"PLC: {PlcReadValueCount}/{PlcExpectedValueCount} · {_plcReadAtUtc.Value.ToLocalTime():HH:mm:ss} · Stage: {_plcCurrentStage?.ToString() ?? "unavailable"}";

            return string.Join(" · ", new[] { status, _plcError, _plcCurrentStageError }.Where(value => !string.IsNullOrWhiteSpace(value)));
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

        // На время диалога сохраняем последний снимок.
        if (!clearValues) return;

        _plcValues = [];
        _plcReadAtUtc = null;
        _plcError = null;
        _plcCurrentStage = null;
        _plcCurrentStageError = null;
    }

    /// <summary>Опрос принадлежит конкретным Unit и рецепту; отменённые ответы не публикуются.</summary>
    private async Task PollPlcAsync(Task previous, CancellationTokenSource source, int recipeId, int pageId, string layoutStamp)
    {
        var ct = source.Token;

        try
        {
            // Новый цикл ждёт завершения отменённого предыдущего, чтобы запросы не накапливались.
            await previous;
            var quickRetries = 0;

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
                var nextDelay = PlcRefreshInterval;

                try
                {
                    var result = await RecipeApi.ReadPlcAsync(recipeId, pageId, layoutStamp, ct);
                    ct.ThrowIfCancellationRequested();

                    if (result.RecipeId != recipeId || result.PageId != pageId || result.LayoutStamp != layoutStamp || result.CurrentRecipeId <= 0)
                        throw new InvalidOperationException("PLC response does not match the selected recipe.");

                    var values = result.Values.ToDictionary(item => (item.StageId, item.ColumnId));

                    // HTTP 200 ещё не означает, что все отдельные теги прочитались.
                    var incomplete = result.Values.Count < PlcExpectedValueCount || result.Values.Any(item => item.Error is not null || !item.Value.HasValue) || result.CurrentStageError is not null;
                    nextDelay = NextPlcReadDelay(incomplete, ref quickRetries);

                    await InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _disposed) return;

                        _plcValues = values;
                        _plcReadAtUtc = result.ReadAtUtc;

                        var failed = result.Values.FirstOrDefault(item => item.Error is not null || !item.Value.HasValue);
                        _plcError = PlcReadValueCount < PlcExpectedValueCount ? $"Some PLC values are unavailable. {failed?.TagName}: {failed?.Error ?? "A parameter was not returned."}" : null;

                        _plcCurrentStage = result.CurrentStage;
                        _plcCurrentStageError = result.CurrentStageError;

                        var page = _catalog.Pages.FirstOrDefault(item => item.Id == pageId);

                        // Назначение Current могло измениться в другом окне.
                        if (page is not null && page.RecipeId != result.CurrentRecipeId)
                        {
                            page.RecipeId = result.CurrentRecipeId;
                            _selectorRevision++;
                        }
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Изменённую структуру или удалённый рецепт повторным чтением не исправить.
                    stop = ex is HttpRequestException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.BadRequest };

                    if (!stop)
                        nextDelay = NextPlcReadDelay(true, ref quickRetries);

                    Logger.LogWarning(ex, "Recipe PLC read failed. Recipe={RecipeId}, Unit={PageId}.", recipeId, pageId);

                    await InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _disposed) return;

                        _plcValues = [];
                        _plcReadAtUtc = null;
                        _plcError = ex.Message;
                        _plcCurrentStage = null;
                        _plcCurrentStageError = null;
                    });
                }

                await InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _disposed) return;

                    _plcLoading = false;
                    StateHasChanged();
                });

                if (stop) return;

                await Task.Delay(nextDelay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Смена Unit, выключение PLC и закрытие страницы завершают этот цикл.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Recipe PLC polling stopped. Recipe={RecipeId}, Unit={PageId}.", recipeId, pageId);
        }
        finally
        {
            if (ReferenceEquals(_plcStop, source))
                _plcStop = null;

            source.Dispose();
        }
    }

    /// <summary>Три быстрых повторных чтения; затем обычный интервал. К записи не применяется.</summary>
    private static TimeSpan NextPlcReadDelay(bool incomplete, ref int quickRetries)
    {
        if (!incomplete)
        {
            quickRetries = 0;
            return PlcRefreshInterval;
        }

        return quickRetries++ < 3 ? TimeSpan.FromSeconds(1) : PlcRefreshInterval;
    }

    private RecipePlcValueDto? FindPlcValue(RecipeStageDto stage, RecipeColumnDto column) => _plcValues.GetValueOrDefault((stage.Id, column.Id));

    /// <summary>До первого ответа ячейка остаётся пустой; некорректный ответ отображается предупреждением.</summary>
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