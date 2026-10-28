using Microsoft.AspNetCore.WebUtilities;
using TechMES.Contracts.Recipes;
using TechMES.Contracts.Security;

namespace TechMES.Web.Components.Recipe;

/// <summary>
/// Разделяет базовые и рабочие рецепты в редакторе.
/// Базовый режим доступен в интерфейсе только пользователю с ролью Developer.
/// </summary>
public partial class Recipes
{
    private string _recipeScope = "recipes";
    private int? _lastBaseRecipeId, _lastWorkingRecipeId, _lastWorkingPageId;

    // Проверяем общую роль приложения, а не имя Windows-группы внутри компонента.
    private bool CanManageRecipeDefinitions => UserRoles.HasRole(AppRoles.Developer);
    private bool IsBaseRecipeMode => EditorMode && CanManageRecipeDefinitions && _recipeScope == "base";
    private RecipePageDto? CreationPage => SelectedPage ?? _catalog.Pages.FirstOrDefault(page => page.BaseRecipeId == EditorFamilyId);

    /// <summary>
    /// Current определяется по всем Unit, а не только по выбранному.
    /// </summary>
    private bool IsCurrentRecipe(int? recipeId) => recipeId.HasValue && _catalog.Pages.Any(page => page.RecipeId == recipeId.Value);

    /// <summary>
    /// Восстанавливает режим и Unit при первом открытии редактора.
    /// Ссылка scope=base не включает базовый режим пользователю без Developer.
    /// </summary>
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

        // Базовый режим восстанавливаем только при наличии Developer.
        if (CanManageRecipeDefinitions && (query.TryGetValue("scope", out var scope) ? scope.ToString() == "base" : isBase))
        {
            _recipeScope = "base";
            _lastBaseRecipeId = isBase ? recipeId : familyId ?? SelectedPage?.BaseRecipeId;
            _selectedPageId = null;
        }
    }

    /// <summary>
    /// Меняет режим после подтверждения отказа от несохранённого черновика.
    /// Проверка Developer соответствует видимости переключателя.
    /// </summary>
    private async Task ChangeRecipeScopeAsync(string value)
    {
        if (!CanManageRecipeDefinitions || !EditorMode || value is not ("base" or "recipes") || value == _recipeScope || IsBusy || _dialogOpen) return;
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

    /// <summary>
    /// В режиме Base recipes показывает только рецепты с соответствующим началом имени.
    /// В рабочем режиме сохраняет существующую фильтрацию по Unit и семейству.
    /// </summary>
    private void SelectAvailableRecipes(int? preferredRecipeId)
    {
        var baseIds = _catalog.Pages.Select(page => page.BaseRecipeId).ToHashSet();
        var page = SelectedPage;

        if (IsBaseRecipeMode)
        {
            // Проверяем начало имени без учёта регистра: Base, base и BASE подходят.
            _availableRecipes = _catalog.Recipes.Where(recipe => recipe.Name.StartsWith("Base", StringComparison.OrdinalIgnoreCase)).ToList();
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