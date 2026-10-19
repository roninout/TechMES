using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Выбор и изменение существующих стадий сохранённого рецепта.
/// </summary>
public partial class Recipes
{
    private IList<RecipeStageDto> _selectedStages = [];
    private int? _stageSelectionRecipeId;
    private bool _changingStage;

    private RecipeStageDto? SelectedStage => Stages.FirstOrDefault(stage => stage.Id == _selectedStages.FirstOrDefault()?.Id);
    private int SelectedStageIndex => _snapshot?.Stages.FindIndex(stage => stage.Id == SelectedStage?.Id) ?? -1;
    private bool CanChangeStage => CanManageRecipe && _editValues && SelectedStage is not null;
    private bool CanMoveStageUp => CanChangeStage && SelectedStageIndex > 0;
    private bool CanMoveStageDown => CanChangeStage && SelectedStageIndex >= 0 && SelectedStageIndex < Stages.Count - 1;

    /// <summary>
    /// Выделяет объект из нового снимка, а не ссылку на строку старой таблицы.
    /// </summary>
    private void RestoreStageSelection(int? preferredStageId)
    {
        _stageSelectionRecipeId = _snapshot?.Recipe.Id;

        var stage = Stages.FirstOrDefault(item => item.Id == preferredStageId) ?? Stages.FirstOrDefault();
        _selectedStages = stage is null ? [] : [stage];
    }

    /// <summary>
    /// Отправляет одну операцию и перечитывает фактически сохранённые стадии.
    /// Удаление требует подтверждения и удаляет также параметры выбранной стадии.
    /// </summary>
    private async Task ChangeStageAsync(RecipeStageChangeOperation operation)
    {
        var stage = SelectedStage;

        if (!CanChangeStage || _disposed || _snapshot is null || stage is null)
            return;

        var recipeId = _snapshot.Recipe.Id;
        var ct = _stop.Token;
        var request = new RecipeStageChangeRequest
        {
            Operation = operation,
            StageId = stage.Id,
            Auto = operation == RecipeStageChangeOperation.SetAuto ? !stage.Auto : null,
            ExpectedStages = Stages.Select(item => new RecipeStagePositionDto { Id = item.Id, Number = item.Number, Auto = item.Auto }).ToList()
        };

        var requestStarted = false;
        var saved = false;

        _changingStage = true;
        _error = null;

        try
        {
            if (operation == RecipeStageChangeOperation.Delete)
            {
                _dialogOpen = true;

                var confirmed = await DialogService.Confirm($"Delete stage {stage.Number}: {stage.Name}? All saved parameter values of this stage will also be deleted.", "Delete recipe stage", new ConfirmOptions { OkButtonText = "Delete", CancelButtonText = "Cancel" });

                _dialogOpen = false;
                ct.ThrowIfCancellationRequested();

                if (confirmed != true)
                    return;
            }

            requestStarted = true;

            var result = await RecipeApi.ChangeStageAsync(recipeId, request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved)
                throw new InvalidOperationException("Unexpected stage operation response. Press Refresh.");

            saved = true;
            _loading = true;

            await InvokeAsync(StateHasChanged);

            var snapshot = await RecipeApi.GetAsync(recipeId, ct);
            ct.ThrowIfCancellationRequested();

            _snapshot = snapshot;
            RestoreStageSelection(result.SelectedStageId);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Stage changes saved to database.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // После ухода со страницы операцию автоматически не повторяем.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Recipe stage operation failed. Recipe={RecipeId}, Stage={StageId}, Operation={Operation}.", recipeId, stage.Id, operation);

                // После ошибки запроса сначала требуется получить актуальное состояние.
                if (requestStarted)
                {
                    _snapshot = null;
                    RestoreStageSelection(null);
                }

                _error = saved ? "Stage changes were saved, but the recipe could not be reloaded. Press Refresh. " + ex.Message
                    : ex is HttpRequestException { StatusCode: not null } ? ex.Message : "Operation result could not be confirmed. Press Refresh before retrying. " + ex.Message;
            }
        }
        finally
        {
            _dialogOpen = false;
            _changingStage = false;
            _loading = false;
        }
    }
}