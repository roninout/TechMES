using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>Добавляет шаблон перетаскиванием с автоматическим назначением prefix на сервере.</summary>
public partial class Recipes
{
    private bool CanAddStage => EditorMode && CanManageRecipe && _editValues;

    /// <summary>Вставляет шаблон перед строкой или в конец и перечитывает сохранённый рецепт.</summary>
    private async Task AddStageAsync(int templateId, int? beforeStageId)
    {
        if (!CanAddStage || _disposed || _snapshot is null || !_stageTemplates.Templates.Any(item => item.Id == templateId)) return;

        var recipeId = _snapshot.Recipe.Id;
        var ct = _stop.Token;
        var request = new RecipeStageAddRequest
        {
            BaseStageId = templateId,
            AutoAssignTagPrefix = true,
            RecipePageId = SelectedPage?.Id,
            InsertBeforeStageId = beforeStageId,
            ExpectedStages = Stages.Select(item => new RecipeStagePositionDto { Id = item.Id, Number = item.Number, Auto = item.Auto }).ToList()
        };

        var requestStarted = false;
        var saved = false;
        _changingStage = true;
        _loading = true;
        _error = null;

        try
        {
            requestStarted = true;
            var result = await RecipeApi.AddStageAsync(recipeId, request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.SelectedStageId is null)
                throw new InvalidOperationException("Unexpected add stage response. Press Refresh.");

            saved = true;
            await LoadSelectedRecipeAsync(ct);
            RestoreStageSelection(result.SelectedStageId);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Stage and its parameters added to database.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // При уходе со страницы вставку автоматически не повторяем.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Failed to add template {TemplateId} to recipe {RecipeId}.", templateId, recipeId);

                if (requestStarted)
                {
                    _snapshot = null;
                    RestoreStageSelection(null);
                }

                _error = saved ? "The stage was added, but the recipe could not be reloaded. Press Refresh. " + ex.Message
                    : ex is HttpRequestException { StatusCode: not null } ? ex.Message : "Add stage result could not be confirmed. Press Refresh before retrying. " + ex.Message;
            }
        }
        finally
        {
            _changingStage = false;
            _loading = false;
        }
    }
}