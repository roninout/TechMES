using Radzen;
using TechMES.Contracts.Param;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    private bool _canWritePlc;
    private bool _writingPlc;

    /// <summary>Недоступное значение нельзя использовать как исходное для записи.</summary>
    private bool CanWritePlcParameter(RecipeStageDto stage, RecipeColumnDto column) => _canWritePlc && CanReadPlc && _plcEnabled && !IsBusy && !_dialogOpen && FindPlcValue(stage, column) is { Value: not null, Error: null };

    private Task OnRecipeValueClickedAsync(RecipeStageDto stage, RecipeColumnDto column) => EditorMode ? EditParameterAsync(stage, column) : WritePlcParameterAsync(stage, column);

    /// <summary>В просмотре меняет только PLC; для дискретки предлагает противоположное значение.</summary>
    private async Task WritePlcParameterAsync(RecipeStageDto stage, RecipeColumnDto column)
    {
        if (!CanWritePlcParameter(stage, column) || _snapshot is null || SelectedPage is null) return;

        var current = FindPlcValue(stage, column)!;
        var recipeId = _snapshot.Recipe.Id;

        var request = new RecipePlcWriteRequest { PageId = SelectedPage.Id, StageId = stage.Id, ColumnId = column.Id, ExpectedValue = current.Value!.Value, LayoutStamp = RecipePlcLayout.GetStamp(_snapshot) };

        // Текущее значение сохраняем настоящим: оно используется для подписи и проверки перед записью.
        var item = new ParamItemDto { Name = column.Name, Kind = column.Kind == RecipeParameterKind.CheckBox ? ParamValueKind.Boolean : ParamValueKind.Number, NumericValue = current.Value, BooleanValue = column.Kind == RecipeParameterKind.CheckBox ? current.Value != 0 : null, ValueText = FormatValue(current.Value.Value, column.Format), Unit = column.Description, CanWrite = true };

        var description = $"Recipe [{recipeId}] {_snapshot.Recipe.Name}; stage {stage.Number}: {column.Name}";
        var ct = _stop.Token;

        _writingPlc = true;
        StopPlcReading(clearValues: false);

        try
        {
            await _plcTask;
            ct.ThrowIfCancellationRequested();

            _dialogOpen = true;

            await ParamWriter.WriteAsync($"{SelectedPage.Page} / stage {stage.Number}", item, (write, token) =>
            {
                write.Comment = description;
                request.Write = write;

                return RecipeApi.WritePlcParameterAsync(recipeId, request, token);
            }, column.Kind == RecipeParameterKind.CheckBox && current.Value == 0, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
                NotificationService.Notify(NotificationSeverity.Error, "Recipe PLC write", ex.Message, 4500);
        }
        finally
        {
            _dialogOpen = false;
            _writingPlc = false;

            if (!_disposed)
                StartPlcReading();
        }
    }

}