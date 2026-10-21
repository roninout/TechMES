using Microsoft.Extensions.Logging;
using Radzen;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Управление рецептами.
/// Поля и существующая логика просмотра остаются в Recipes.razor.
/// </summary>
public partial class Recipes
{
    private bool _managingRecipe;

    private int? EditorFamilyId => SelectedPage?.BaseRecipeId ?? (_snapshot is null ? RecipeEditorNavigation.ReadFamily(Navigation.Uri) : _catalog.Pages.Any(page => page.BaseRecipeId == _snapshot.Recipe.Id) ? _snapshot.Recipe.Id : _snapshot.Recipe.BaseRecipeId);
    private bool CanManageRecipe => _canEdit && !IsBusy && _snapshot is not null;
    private int? NewRecipeSourceId => _snapshot?.Recipe.Id ?? SelectedPage?.BaseRecipeId;

    /// <summary>При первом открытии восстанавливает Unit и рецепт из ссылки, затем сохраняет текущий выбор.</summary>
    private async Task ReloadCatalogAndRecipeAsync(int? preferredRecipeId, CancellationToken ct)
    {
        var catalog = await RecipeApi.GetCatalogAsync(ct);
        ct.ThrowIfCancellationRequested();

        var firstLoad = !_catalogLoaded;
        _catalog = catalog;

        if (firstLoad)
        {
            RestoreInitialUnit();
        }
        else if (_selectedPageId.HasValue && !_catalog.Pages.Any(page => page.Id == _selectedPageId))
        {
            _selectedPageId = _catalog.Pages.FirstOrDefault()?.Id;
        }

        _catalogLoaded = true;

        SelectAvailableRecipes(preferredRecipeId ?? (firstLoad ? ReadQueryId("recipe") : null));
        await LoadSelectedRecipeAsync(ct);
    }

    /// <summary>
    /// Предлагает свободное по текущему каталогу имя.
    /// Окончательная проверка выполняется в БД.
    /// </summary>
    private string SuggestRecipeName(RecipeChangeOperation operation)
    {
        if (operation == RecipeChangeOperation.Rename)
            return _snapshot?.Recipe.Name ?? "";

        var basis = operation == RecipeChangeOperation.Copy ? $"{_snapshot?.Recipe.Name} - copy"
            : string.IsNullOrWhiteSpace(SelectedPage?.Page) ? "New recipe" : $"{SelectedPage.Page} - New recipe";

        var suffix = 1;

        while (_catalog.Recipes.Any(recipe => recipe.Name.Equals($"{basis} {suffix}", StringComparison.OrdinalIgnoreCase)))
            suffix++;

        return $"{basis} {suffix}";
    }

    /// <summary>
    /// Открывает ввод имени и выполняет одну операцию с последующим выбором результата.
    /// </summary>
    private async Task ChangeRecipeAsync(RecipeChangeOperation operation)
    {
        var sourceId = operation == RecipeChangeOperation.Create ? NewRecipeSourceId : _snapshot?.Recipe.Id;

        if (IsBusy || _disposed || !_canEdit || sourceId is null)
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
                RecipeChangeOperation.Create => "Creates an empty recipe in the same recipe family.",
                RecipeChangeOperation.Copy => "Copies the saved recipe, including all stages and parameter values.",
                _ => "Changes only the recipe name."
            };

            _dialogOpen = true;

            var input = await DialogService.OpenAsync<RecipeNameDialog>(title, new Dictionary<string, object?>
            {
                [nameof(RecipeNameDialog.InitialName)] = SuggestRecipeName(operation),
                [nameof(RecipeNameDialog.Description)] = description,
                [nameof(RecipeNameDialog.RequiredNamePart)] = SelectedPage?.Page
            }, new DialogOptions { Width = "560px", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true });

            _dialogOpen = false;
            ct.ThrowIfCancellationRequested();

            if (input is not string name)
                return;

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
            // Страница закрыта; повторно операцию автоматически не отправляем.
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