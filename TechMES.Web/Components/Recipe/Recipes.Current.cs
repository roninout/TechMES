using System.Net;
using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    private bool _settingCurrent;

    private string SelectedRecipeTitle => _availableRecipes.FirstOrDefault(recipe => recipe.Id == _selectedRecipeId)?.DisplayName ?? "Recipes";
    private bool IsSelectedRecipeCurrent => _selectedRecipeId.HasValue && SelectedPage?.RecipeId == _selectedRecipeId.Value;
    private bool CanSetCurrentRecipe => !EditorMode && !_plcEnabled && _canWritePlc && !IsBusy && !_dialogOpen && !_disposed && !_uncertainDraft && _snapshot is not null && SelectedPage is not null && _snapshot.Recipe.Id == _selectedRecipeId && !IsSelectedRecipeCurrent && !IsAssignedBaseRecipe(_selectedRecipeId) && _snapshot.Recipe.BaseRecipeId == SelectedPage.BaseRecipeId;

    /// <summary>Назначает выбранный рецепт текущим для одного Unit, сохраняя режим просмотра БД.</summary>
    private async Task SetCurrentRecipeAsync()
    {
        if (!CanSetCurrentRecipe || _snapshot is null || SelectedPage is null) return;

        var original = CloneRecipe(_snapshot);
        var page = SelectedPage;
        var request = new RecipeChangeRequest
        {
            Operation = RecipeChangeOperation.SetCurrent,
            SourceRecipeId = original.Recipe.Id,
            ExpectedSnapshot = original,
            ExpectedBaseRecipeId = page.BaseRecipeId,
            ExpectedPages = [new() { Id = page.Id, Page = page.Page, RecipeId = page.RecipeId, BaseRecipeId = page.BaseRecipeId }]
        };

        var ct = _stop.Token;
        var requestSent = false;
        var assigned = false;

        _settingCurrent = true;
        _error = null;

        try
        {
            _dialogOpen = true;

            var message = $"Set {original.Recipe.DisplayName} as Current for Unit {page.Page}? This updates the database assignment only. PLC values will remain unchanged.";
            var confirmed = await DialogService.Confirm(message, "Set as current", new ConfirmOptions { OkButtonText = "Set as current", CancelButtonText = "Cancel" });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (confirmed != true) return;

            // Запрос назначения не повторяем автоматически при потере ответа.
            requestSent = true;
            var result = await RecipeApi.ChangeAsync(request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.RecipeId != original.Recipe.Id)
                throw new InvalidOperationException("Unexpected Current recipe assignment response.");

            assigned = true;
            page.RecipeId = original.Recipe.Id;
            _selectorRevision++;
            _loading = true;

            await InvokeAsync(StateHasChanged);
            await ReloadCatalogAndRecipeAsync(original.Recipe.Id, ct);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", $"Current recipe assigned for Unit {page.Page}.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _uncertainDraft = !assigned && requestSent && ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };
                _error = assigned ? "Current recipe assigned, but the page could not be refreshed. Press Refresh. " + ex.Message
                    : _uncertainDraft ? "Assignment result is uncertain. Press Refresh before trying again. " + ex.Message : ex.Message;

                Logger.LogWarning(ex, "Failed to assign Current recipe. Recipe={RecipeId}, Unit={PageId}.", original.Recipe.Id, page.Id);
            }
        }
        finally
        {
            _dialogOpen = false;
            _settingCurrent = false;
            _loading = false;
        }
    }
}