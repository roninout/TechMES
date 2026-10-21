using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace TechMES.Web.Components.Common;

/// <summary>Разделяет просмотр и редактор, сохраняя выбранные Unit и рецепт при переходе.</summary>
public partial class Recipes
{
    [Parameter] public bool EditorMode { get; set; }

    /// <summary>Читает положительный идентификатор из адреса страницы.</summary>
    private int? ReadQueryId(string name)
    {
        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);

        return query.TryGetValue(name, out var text) && int.TryParse(text.ToString(), out var id) && id > 0 ? id : null;
    }

    /// <summary>Восстанавливает Unit; отдельное значение all сохраняет режим All units.</summary>
    private void RestoreInitialUnit()
    {
        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);

        if (query.TryGetValue("unit", out var unit) && unit.ToString().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            _selectedPageId = null;
            return;
        }

        var unitId = ReadQueryId("unit");
        var familyId = RecipeEditorNavigation.ReadFamily(Navigation.Uri);
        var page = _catalog.Pages.FirstOrDefault(item => item.Id == unitId) ?? _catalog.Pages.FirstOrDefault(item => item.BaseRecipeId == familyId) ?? _catalog.Pages.FirstOrDefault();

        _selectedPageId = page?.Id;
    }

    /// <summary>Открывает редактор с тем же рецептом; незавершённая операция блокирует переход.</summary>
    private void OpenEditor() => OpenRecipePage("/recipes/editor");

    /// <summary>Возвращает пользователя к просмотру выбранного рецепта.</summary>
    private void OpenRecipes() => OpenRecipePage("/recipes");

    /// <summary>Передаёт выбор через адрес, не создавая дополнительного состояния или повторных записей в БД.</summary>
    private void OpenRecipePage(string path)
    {
        if (IsBusy || _dialogOpen || _disposed)
            return;

        var query = new Dictionary<string, string?>
        {
            ["family"] = EditorFamilyId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["recipe"] = _selectedRecipeId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["unit"] = _selectedPageId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"
        };

        Navigation.NavigateTo(QueryHelpers.AddQueryString(path, query));
    }
}