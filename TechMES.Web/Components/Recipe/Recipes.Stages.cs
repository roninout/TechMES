using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    private IList<RecipeStageDto> _selectedStages = [];
    private int? _stageSelectionRecipeId;
    private bool _changingStage;

    private RecipeStageDto? SelectedStage => Stages.FirstOrDefault(item => item.Id == _selectedStages.FirstOrDefault()?.Id);
    private int SelectedStageIndex => _snapshot?.Stages.FindIndex(item => item.Id == SelectedStage?.Id) ?? -1;

    /// <summary>Восстанавливает выделение объектом текущего черновика.</summary>
    private void RestoreStageSelection(int? preferredStageId)
    {
        _stageSelectionRecipeId = _snapshot?.Recipe.Id;

        var stage = Stages.FirstOrDefault(item => item.Id == preferredStageId) ?? Stages.FirstOrDefault();

        _selectedStages = stage is null ? [] : [stage];
    }

    /// <summary>Переносит или удаляет в черновике; запись в БД выполняется кнопкой Save.</summary>
    private async Task ChangeStageAsync(RecipeStageChangeOperation operation, RecipeStageDto? row = null, int? targetIndex = null)
    {
        var stage = row is null ? SelectedStage : Stages.FirstOrDefault(item => item.Id == row.Id);

        if (!CanAddStage || _snapshot is null || stage is null) return;

        if (operation == RecipeStageChangeOperation.Delete)
        {
            _dialogOpen = true;

            try
            {
                if (await DialogService.Confirm($"Remove stage {stage.Number}: {stage.Name}? The deletion will be applied when you press Save.", "Remove recipe stage", new ConfirmOptions { OkButtonText = "Remove", CancelButtonText = "Cancel" }) != true || _disposed)
                    return;
            }
            finally
            {
                _dialogOpen = false;
            }

            _snapshot.Stages.Remove(stage);
        }
        else if (operation == RecipeStageChangeOperation.MoveTo && targetIndex is int target && target >= 0 && target < Stages.Count)
        {
            _snapshot.Stages.Remove(stage);
            _snapshot.Stages.Insert(target, stage);
        }
        else
        {
            return;
        }

        RebuildDraft();
        RestoreStageSelection(stage.Id);
    }
}