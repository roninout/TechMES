using System.Globalization;
using System.Net;
using System.Text.Json;
using Radzen;
using TechMES.Contracts.Recipes;
using Microsoft.AspNetCore.Components;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    private bool _savingValues;

    private bool HasCompletePlcSnapshot => _plcEnabled && _plcReadAtUtc.HasValue && _plcError is null && PlcExpectedValueCount > 0 && PlcReadValueCount == PlcExpectedValueCount;
    private bool CanSavePlcValues => _canWritePlc && CanReadPlc && !IsBusy && !_dialogOpen && !_uncertainDraft && !_plcLoading && HasCompletePlcSnapshot;

    /// <summary>Малую погрешность представления REAL не считаем изменением значения.</summary>
    private static int CompareRecipeValues(double plc, double database)
    {
        if (!double.IsFinite(plc) || !double.IsFinite(database)) return 0;

        var tolerance = Math.Max(1e-9, Math.Max(Math.Abs(plc), Math.Abs(database)) * 1e-7);

        return Math.Abs(plc - database) <= tolerance ? 0 : plc > database ? 1 : -1;
    }

    /// <summary>Стрелка сравнивает PLC с сохранённой БД, а не с предыдущим опросом.</summary>
    private int RecipeValueDirection(RecipeStageDto stage, RecipeColumnDto column)
    {
        if (!_plcEnabled || column.Kind != RecipeParameterKind.InputField || !stage.Parameters.TryGetValue(column.Id, out var parameter)) return 0;

        var item = FindPlcValue(stage, column);

        return IsReadablePlcValue(item, column.Kind) ? CompareRecipeValues(item!.Value!.Value, parameter.Value) : 0;
    }

    private static string RecipeDifferenceTitle(double plc, double database) => $"PLC: {plc.ToString("G", CultureInfo.CurrentCulture)}; DB: {database.ToString("G", CultureInfo.CurrentCulture)}";

    /// <summary>Сохраняет показанный PLC-снимок в выбранный рецепт после подтверждения.</summary>
    private async Task SavePlcValuesToDbAsync()
    {
        if (!CanSavePlcValues || _snapshot is null) return;

        var ct = _stop.Token;
        var requestSent = false;

        _savingValues = true;
        _error = null;
        StopPlcReading(clearValues: false);

        try
        {
            // Во время подтверждения снимок не меняется, новый опрос не запускается.
            await _plcTask;
            ct.ThrowIfCancellationRequested();

            if (!HasCompletePlcSnapshot)
                throw new InvalidOperationException("Read every PLC value successfully before saving to the database.");

            var recipeId = _snapshot.Recipe.Id;
            var request = new RecipeValuesSaveRequest
            {
                Original = CloneRecipe(_snapshot),
                Values = Stages.SelectMany(stage => stage.Parameters.Keys.Select(columnId => new RecipeValueSaveItem { StageId = stage.Id, ColumnId = columnId, Value = _plcValues[(stage.Id, columnId)].Value!.Value })).ToList()
            };

            // Данные снимка и последствия сохранения показываем отдельными строками.
            var description = string.Join("\n",
                $"Recipe: {_snapshot.Recipe.DisplayName}",
                $"PLC values: {request.Values.Count}",
                $"Read at: {_plcReadAtUtc!.Value.ToLocalTime():dd.MM.yyyy HH:mm:ss}",
                "",
                "The displayed PLC values will be saved to the database.",
                "Existing database values will be replaced.",
                "",
                "Save these values to the recipe?");

            // Переносы строк сохраняются, названия из БД выводятся обычным текстом.
            RenderFragment message = builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "style", "white-space: pre-line; line-height: 1.6; text-align: left;");
                builder.AddContent(2, description);
                builder.CloseElement();
            };

            _dialogOpen = true;

            var confirmed = await DialogService.Confirm(message, "Save to DB", new ConfirmOptions { Width = "560px", OkButtonText = "Save to DB", CancelButtonText = "Cancel" });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (confirmed != true) return;

            // POST не повторяем автоматически: результат записи может быть неопределённым.
            requestSent = true;

            var result = await RecipeApi.SaveValuesAsync(recipeId, request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.Snapshot is null || result.Snapshot.Recipe.Id != recipeId)
                throw new InvalidOperationException("Unexpected recipe value save response.");

            var stageId = _selectedStages.FirstOrDefault()?.Id;

            _snapshot = result.Snapshot;
            _savedSnapshot = CloneRecipe(_snapshot);
            _savedRecipeStamp = JsonSerializer.Serialize(_savedSnapshot);
            _hasRecipeChanges = false;

            RestoreStageSelection(stageId);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "PLC values saved to database.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                // При потере ответа сначала перечитываем БД кнопкой Refresh.
                _uncertainDraft = requestSent && ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };
                _error = _uncertainDraft ? "Save result is uncertain. Press Refresh before saving again. " + ex.Message : ex.Message;

                Logger.LogWarning(ex, "Saving recipe PLC values to database failed.");
            }
        }
        finally
        {
            _dialogOpen = false;
            _savingValues = false;

            if (!_disposed)
                StartPlcReading();
        }
    }

}