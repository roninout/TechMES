using System.Net;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

public partial class Recipes
{
    private bool _settingCurrent;

    private string SelectedRecipeTitle => _availableRecipes.FirstOrDefault(recipe => recipe.Id == _selectedRecipeId)?.DisplayName ?? "Recipes";
    private string RecipeViewTitle => _plcEnabled ? SelectedRecipeTitle : $"DB values: {SelectedRecipeTitle}";
    private string RecipeViewTitleClass => !_plcEnabled ? "is-database" : SelectedPage is not null && _selectedRecipeId.HasValue && !IsSelectedRecipeCurrent ? "is-plc-other" : "";
    private bool IsSelectedRecipeCurrent => _selectedRecipeId.HasValue && SelectedPage?.RecipeId == _selectedRecipeId.Value;

    // Current тоже разрешено загрузить повторно после сохранения изменённых значений.
    private bool CanSetCurrentRecipe => EditorMode && !IsBaseRecipeMode && _canEdit && _canWritePlc && !IsBusy && !_dialogOpen && !_disposed && !_uncertainDraft && !HasRecipeChanges && _snapshot is not null && SelectedPage is not null && _snapshot.Recipe.Id == _selectedRecipeId && !IsAssignedBaseRecipe(_selectedRecipeId) && _snapshot.Recipe.BaseRecipeId == SelectedPage.BaseRecipeId;

    /// <summary>Загружает сохранённые БД-значения; черновик сначала нужно сохранить кнопкой Save.</summary>
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
            ExpectedPages = [new() { Id = page.Id, Page = page.Page, RecipeId = page.RecipeId, BaseRecipeId = page.BaseRecipeId, CurrentStageTag = page.CurrentStageTag, ModeTag = page.ModeTag }]
        };

        var ct = _stop.Token;
        var requestSent = false;
        var assigned = false;

        _settingCurrent = true;
        _error = null;

        try
        {
            _dialogOpen = true;

            var message = $"Load saved database values of {original.Recipe.DisplayName} into Unit {page.Page} and set this recipe as Current? PLC recipe values will be overwritten. The Unit must be in manual mode. Continue?";
            var confirmed = await DialogService.Confirm(message, "Warning: load recipe to PLC", new ConfirmOptions { OkButtonText = "Load and set Current", CancelButtonText = "Cancel" });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (confirmed != true) return;

            // Команду записи автоматически не повторяем при потере HTTP-ответа.
            requestSent = true;
            var result = await RecipeApi.ChangeAsync(request, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.RecipeId != original.Recipe.Id)
                throw new InvalidOperationException("Unexpected PLC recipe loading response.");

            assigned = true;
            page.RecipeId = original.Recipe.Id;
            _selectorRevision++;
            _loading = true;

            await InvokeAsync(StateHasChanged);
            await ReloadCatalogAndRecipeAsync(original.Recipe.Id, ct);

            NotificationService.Notify(result.Warning is null ? NotificationSeverity.Success : NotificationSeverity.Warning, "Recipe", result.Warning ?? $"Recipe loaded to PLC and assigned Current for Unit {page.Page}.", 4500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _uncertainDraft = !assigned && requestSent && ex is not HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound };
                _error = assigned ? "Recipe loaded, but the page could not be refreshed. Press Refresh. " + ex.Message
                    : _uncertainDraft ? "Loading outcome is uncertain. Check PLC and press Refresh before trying again. " + ex.Message : ex.Message;

                Logger.LogWarning(ex, "Failed to load Current recipe. Recipe={RecipeId}, Unit={PageId}.", original.Recipe.Id, page.Id);
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