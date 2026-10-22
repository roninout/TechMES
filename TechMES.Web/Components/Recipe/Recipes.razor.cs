using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Recipe;

/// <summary>Управление заголовками рецептов и загрузкой выбранного семейства.</summary>
public partial class Recipes
{
    private bool _managingRecipe;

    private int? EditorFamilyId => SelectedPage?.BaseRecipeId ?? (_snapshot is null ? RecipeEditorNavigation.ReadFamily(Navigation.Uri) : _catalog.Pages.Any(page => page.BaseRecipeId == _snapshot.Recipe.Id) ? _snapshot.Recipe.Id : _snapshot.Recipe.BaseRecipeId);
    private bool CanManageRecipe => _canEdit && !IsBusy && !_dialogOpen && !_uncertainDraft && _snapshot is not null;
    private int? NewRecipeSourceId => _snapshot?.Recipe.Id ?? SelectedPage?.BaseRecipeId;

    /// <summary>Восстанавливает Unit и загружает Current при первом открытии просмотра.</summary>
    private async Task ReloadCatalogAndRecipeAsync(int? preferredRecipeId, CancellationToken ct)
    {
        var catalog = await RecipeApi.GetCatalogAsync(ct);
        ct.ThrowIfCancellationRequested();

        var firstLoad = !_catalogLoaded;
        _catalog = catalog;

        if (firstLoad)
        {
            RestoreRecipeScope();
        }
        else if (_selectedPageId.HasValue && !_catalog.Pages.Any(page => page.Id == _selectedPageId))
        {
            _selectedPageId = _catalog.Pages.FirstOrDefault()?.Id;
        }

        // В просмотре всегда нужен конкретный Unit; старую ссылку unit=all восстанавливаем на первый.
        if (firstLoad && !EditorMode && SelectedPage is null)
            _selectedPageId = _catalog.Pages.FirstOrDefault()?.Id;

        _catalogLoaded = true;

        // При первом открытии просмотра выбираем Current. Refresh сохраняет ручной выбор.
        var recipeId = firstLoad && !EditorMode ? SelectedPage?.RecipeId : preferredRecipeId ?? (firstLoad ? ReadQueryId("recipe") : null);
        SelectAvailableRecipes(recipeId);

        await LoadSelectedRecipeAsync(ct);
    }

    /// <summary>Предлагает свободное имя; окончательная проверка выполняется сервером.</summary>
    private string SuggestRecipeName(RecipeChangeOperation operation)
    {
        if (operation == RecipeChangeOperation.Rename)
            return _snapshot?.Recipe.Name ?? "";

        var basis = operation == RecipeChangeOperation.Copy ? $"{_snapshot?.Recipe.Name} - copy" : "New recipe";
        var pageName = CreationPage?.Page;

        if (!string.IsNullOrWhiteSpace(pageName) && !basis.Contains(pageName, StringComparison.OrdinalIgnoreCase))
            basis = $"{pageName} - {basis}";

        var suffix = 1;

        while (_catalog.Recipes.Any(recipe => recipe.Name.Equals($"{basis} {suffix}", StringComparison.OrdinalIgnoreCase)))
            suffix++;

        return $"{basis} {suffix}";
    }

    /// <summary>New/Copy создают рецепт через прежний API; Rename изменяет черновик до Save.</summary>
    private async Task ChangeRecipeAsync(RecipeChangeOperation operation)
    {
        var sourceId = operation == RecipeChangeOperation.Create ? NewRecipeSourceId : _snapshot?.Recipe.Id;

        if (IsBusy || _disposed || !_canEdit || _dialogOpen || _uncertainDraft || sourceId is null)
            return;

        if (operation != RecipeChangeOperation.Rename && !await SaveBeforeManagementAsync())
            return;

        var expectedName = _snapshot?.Recipe.Name;
        var ct = _stop.Token;
        int? savedRecipeId = null;

        _managingRecipe = true;
        _error = null;

        try
        {
            var title = operation switch
            {
                RecipeChangeOperation.Create => "New recipe",
                RecipeChangeOperation.Copy => "Copy recipe",
                _ => "Rename recipe"
            };

            var description = operation switch
            {
                RecipeChangeOperation.Create => "Creates an empty working recipe in the same recipe family.",
                RecipeChangeOperation.Copy => "Creates a working copy in the same family, including all stages and parameter values.",
                _ => "Changes the draft name. Apply it with the main Save button."
            };

            _dialogOpen = true;

            var input = await DialogService.OpenAsync<RecipeNameDialog>(title, new Dictionary<string, object?>
            {
                [nameof(RecipeNameDialog.InitialName)] = SuggestRecipeName(operation),
                [nameof(RecipeNameDialog.Description)] = description,
                [nameof(RecipeNameDialog.RequiredNamePart)] = operation == RecipeChangeOperation.Rename ? SelectedPage?.Page : CreationPage?.Page
            }, new DialogOptions { Width = "560px", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (input is not string name)
                return;

            if (operation == RecipeChangeOperation.Rename)
            {
                _snapshot!.Recipe.Name = name;
                MarkRecipeChanges();
                return;
            }

            var result = await RecipeApi.ChangeAsync(new RecipeChangeRequest
            {
                Operation = operation,
                SourceRecipeId = sourceId.Value,
                Name = name,
                ExpectedName = expectedName
            }, ct);

            ct.ThrowIfCancellationRequested();

            if (result.Status != RecipeChangeStatus.Saved || result.RecipeId is not int recipeId)
                throw new InvalidOperationException("Unexpected recipe operation response. Refresh the list.");

            // New/Copy, как и раньше, создают рабочий рецепт, а не новое семейство.
            if (IsBaseRecipeMode)
            {
                _selectedPageId = CreationPage?.Id;
                _recipeScope = "recipes";
                _selectorRevision++;
            }

            savedRecipeId = recipeId;
            _selectedRecipeId = recipeId;
            _loading = true;
            _snapshot = null;

            await InvokeAsync(StateHasChanged);
            await ReloadCatalogAndRecipeAsync(recipeId, ct);

            NotificationService.Notify(NotificationSeverity.Success, "Recipe", "Recipe saved to database.", 2500);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // После закрытия страницы операцию автоматически не повторяем.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Logger.LogError(ex, "Recipe operation failed. Operation={Operation}, Source={SourceId}.", operation, sourceId);

                _error = savedRecipeId.HasValue ? $"Recipe [{savedRecipeId}] was saved, but the page could not be refreshed. Press Refresh. {ex.Message}"
                    : ex is HttpRequestException { StatusCode: not null } ? ex.Message : "Operation result could not be confirmed. Refresh the list before retrying. " + ex.Message;
            }
        }
        finally
        {
            _dialogOpen = false;
            _managingRecipe = false;
            _loading = false;
        }
    }
}