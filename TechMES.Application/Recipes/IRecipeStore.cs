using TechMES.Contracts.Recipes;

namespace TechMES.Application.Recipes;

/// <summary>
/// Доступ к существующему хранилищу рецептов.
/// WEB и Runtime endpoints не зависят от PostgreSQL.
/// </summary>
public interface IRecipeStore
{
    /// <summary>
    /// Возвращает привязки объектов и заголовки рецептов.
    /// </summary>
    Task<RecipeCatalogDto> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Возвращает рецепт со стадиями и параметрами или null, если рецепт отсутствует.
    /// </summary>
    Task<RecipeSnapshotDto?> GetAsync(int recipeId, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет одно значение после проверки принадлежности и предыдущего значения.
    /// </summary>
    Task<RecipeParameterSaveResult> SaveParameterAsync(int recipeId, RecipeParameterSaveRequest request, CancellationToken ct = default);
}