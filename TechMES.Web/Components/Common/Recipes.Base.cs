using System.Net;
using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>Признак назначенного базового рецепта и управление корнем семейства.</summary>
public partial class Recipes
{
    private bool IsAssignedBaseRecipe(int? recipeId) => recipeId.HasValue && _catalog.Pages.Any(page => page.BaseRecipeId == recipeId.Value);
    private bool CanSetBaseRecipe => IsBaseRecipeMode && CanManageRecipe && !IsAssignedBaseRecipe(_snapshot?.Recipe.Id) && !IsCurrentRecipe(_snapshot?.Recipe.Id) && _snapshot?.Recipe.BaseRecipeId is int familyId && _catalog.Pages.Any(page => page.BaseRecipeId == familyId);
    private string RecipeDeleteHint => IsAssignedBaseRecipe(_selectedRecipeId) ? "An assigned base recipe cannot be deleted." : IsCurrentRecipe(_selectedRecipeId) ? "A Current recipe cannot be deleted." : "Delete the selected recipe";

    /// <summary>Сначала сохраняет черновик, затем показывает все затронутые Unit.</summary>
    private async Task SetBaseRecipeAsync()
    {
        if (!CanSetBaseRecipe || !await SaveBeforeManagementAsync() || !CanSetBaseRecipe || _savedSnapshot is null)
            return;

        var original = CloneRecipe(_savedSnapshot);
        var familyId = original.Recipe.BaseRecipeId!.Value;
        var pages = _catalog.Pages.Where(page => page.BaseRecipeId == familyId).Select(page => new RecipePageDto { Id = page.Id, Page = page.Page, RecipeId = page.RecipeId, BaseRecipeId = page.BaseRecipeId }).ToList();
        var oldBase = _catalog.Recipes.FirstOrDefault(recipe => recipe.Id == familyId);
        var unitNames = string.Join(", ", pages.Select(page => page.Page));
        var message = $"Set {original.Recipe.DisplayName} as base instead of {oldBase?.DisplayName ?? familyId.ToString()}? Units: {unitNames}. Parameters, stage templates and recipes will retain their family links. Current recipes and saved values will remain unchanged.";

        _dialogOpen = true;
        bool accepted;

        try
        {
            accepted = await DialogService.Confirm(message, "Assign base recipe", new ConfirmOptions { OkButtonText = "Set as base", CancelButtonText = "Cancel" }) == true;
        }
        finally
        {
            _dialogOpen = false;
        }

        if (!accepted || _disposed) return;

        var ct = _stop.Token;
        var assigned = false;

        _managingRecipe = true;
        _error = null;

        try
        {
            var result = await RecipeApi.ChangeAsync(new RecipeChangeRequest
            {
                Operation = RecipeChangeOperation.SetBase,
                SourceRecipeId = original.Recipe.Id,
                ExpectedSnapshot = original,
                ExpectedBaseRecipeId = familyId,
                ExpectedPages = pages
            }, ct);

            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.RecipeId != original.Recipe.Id)
                throw new InvalidOperationException("Unexpected base recipe assignment response.");

            assigned = true;
            _lastBaseRecipeId = original.Recipe.Id;
            _selectedRecipeId = original.Recipe.Id;
            _snapshot = _savedSnapshot = null;
            _hasRecipeChanges = false;
            _loading = true;

            ClearStageDrag();

            await InvokeAsync(StateHasChanged);
            await ReloadCatalogAndRecipeAsync(original.Recipe.Id, ct);

            _selectorRevision++;

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Base recipe assigned.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Failed to assign base recipe {RecipeId}.", original.Recipe.Id);

                _uncertainDraft = ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };
                _error = assigned ? "Base recipe assigned, but the page could not be refreshed. Press Refresh. " + ex.Message
                    : _uncertainDraft ? "Assignment result is uncertain. Press Refresh before continuing. " + ex.Message : ex.Message;
            }
        }
        finally
        {
            _managingRecipe = false;
            _loading = false;
        }
    }
}