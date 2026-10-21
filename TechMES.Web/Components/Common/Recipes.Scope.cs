using Microsoft.AspNetCore.WebUtilities;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Components.Common;

/// <summary>Разделяет базовые и рабочие рецепты только в редакторе.</summary>
public partial class Recipes
{
    private string _recipeScope = "recipes";
    private int? _lastBaseRecipeId, _lastWorkingRecipeId, _lastWorkingPageId;

    private bool IsBaseRecipeMode => EditorMode && _recipeScope == "base";
    private RecipePageDto? CreationPage => SelectedPage ?? _catalog.Pages.FirstOrDefault(page => page.BaseRecipeId == EditorFamilyId);

    /// <summary>Current определяется по всем Unit, а не только по выбранному.</summary>
    private bool IsCurrentRecipe(int? recipeId) => recipeId.HasValue && _catalog.Pages.Any(page => page.RecipeId == recipeId.Value);

    /// <summary>Восстанавливает режим и Unit при первом открытии редактора.</summary>
    private void RestoreRecipeScope()
    {
        RestoreInitialUnit();

        if (!EditorMode) return;

        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        var recipeId = ReadQueryId("recipe");
        var familyId = RecipeEditorNavigation.ReadFamily(Navigation.Uri);
        var isBase = _catalog.Pages.Any(page => page.BaseRecipeId == recipeId);

        if (_selectedPageId is null)
            _selectedPageId = _catalog.Pages.FirstOrDefault(page => page.BaseRecipeId == familyId)?.Id ?? _catalog.Pages.FirstOrDefault()?.Id;

        _lastWorkingPageId = _selectedPageId;

        if (query.TryGetValue("scope", out var scope) ? scope.ToString() == "base" : isBase)
        {
            _recipeScope = "base";
            _lastBaseRecipeId = isBase ? recipeId : familyId ?? SelectedPage?.BaseRecipeId;
            _selectedPageId = null;
        }
    }

    /// <summary>Меняет режим после подтверждения отказа от несохранённого черновика.</summary>
    private async Task ChangeRecipeScopeAsync(string value)
    {
        if (!EditorMode || value is not ("base" or "recipes") || value == _recipeScope || IsBusy || _dialogOpen) return;
        if (!await DiscardRecipeAsync()) { _selectorRevision++; return; }

        var familyId = EditorFamilyId;

        if (IsBaseRecipeMode)
        {
            _lastBaseRecipeId = _selectedRecipeId;
        }
        else
        {
            _lastWorkingPageId = _selectedPageId;
            _lastWorkingRecipeId = _selectedRecipeId;
        }

        await RunAsync(async ct =>
        {
            _recipeScope = value;
            _selectedPageId = IsBaseRecipeMode ? null : _catalog.Pages.FirstOrDefault(page => page.Id == _lastWorkingPageId)?.Id ?? _catalog.Pages.FirstOrDefault(page => page.BaseRecipeId == familyId)?.Id ?? _catalog.Pages.FirstOrDefault()?.Id;

            SelectAvailableRecipes(IsBaseRecipeMode ? _lastBaseRecipeId ?? familyId : _lastWorkingRecipeId);
            _selectorRevision++;

            await LoadSelectedRecipeAsync(ct);
        });
    }

    /// <summary>Показывает назначенные базовые рецепты и кандидатов; просмотр сохраняет прежний отбор.</summary>
    private void SelectAvailableRecipes(int? preferredRecipeId)
    {
        var baseIds = _catalog.Pages.Select(page => page.BaseRecipeId).ToHashSet();
        var page = SelectedPage;

        if (IsBaseRecipeMode)
        {
            // Показываем назначенные базовые рецепты и кандидатов из существующих семейств.
            _availableRecipes = _catalog.Recipes.Where(recipe => baseIds.Contains(recipe.Id) || (!IsCurrentRecipe(recipe.Id) && recipe.BaseRecipeId is int familyId && baseIds.Contains(familyId))).ToList();
            preferredRecipeId ??= _lastBaseRecipeId;
        }
        else
        {
            var recipes = _catalog.Recipes.Where(recipe => !EditorMode || !baseIds.Contains(recipe.Id));

            _availableRecipes = recipes.Where(recipe => page is null || recipe.Id == page.RecipeId || (recipe.BaseRecipeId == page.BaseRecipeId && recipe.Id != page.BaseRecipeId && recipe.Name.Contains(page.Page, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        _selectedRecipeId = preferredRecipeId.HasValue && _availableRecipes.Any(recipe => recipe.Id == preferredRecipeId) ? preferredRecipeId
            : page is not null && _availableRecipes.Any(recipe => recipe.Id == page.RecipeId) ? page.RecipeId : _availableRecipes.FirstOrDefault()?.Id;
    }
}