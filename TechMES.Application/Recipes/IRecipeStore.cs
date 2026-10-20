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
    /// Возвращает рецепт со стадиями и параметрами или null.
    /// </summary>
    Task<RecipeSnapshotDto?> GetAsync(int recipeId, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет одно значение после проверки принадлежности и предыдущего значения.
    /// </summary>
    Task<RecipeParameterSaveResult> SaveParameterAsync(int recipeId, RecipeParameterSaveRequest request, CancellationToken ct = default);

    /// <summary>
    /// Создаёт пустой рецепт, копирует рецепт или изменяет его имя.
    /// </summary>
    Task<RecipeChangeResult> ChangeAsync(RecipeChangeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Перемещает или удаляет стадию либо изменяет признак автоматического перехода.
    /// </summary>
    Task<RecipeStageChangeResult> ChangeStageAsync(int recipeId, RecipeStageChangeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Получает шаблоны семейства выбранного рецепта или null, если рецепт отсутствует.
    /// </summary>
    Task<RecipeStageTemplateCatalogDto?> GetStageTemplatesAsync(int recipeId, CancellationToken ct = default);

    /// <summary>
    /// Добавляет стадию и параметры её шаблона в одной транзакции.
    /// </summary>
    Task<RecipeStageChangeResult> AddStageAsync(int recipeId, RecipeStageAddRequest request, CancellationToken ct = default);

    /// <summary>Читает семейства и базовые параметры.</summary>
    Task<RecipeBaseParameterCatalogDto> GetBaseParametersAsync(CancellationToken ct = default);

    /// <summary>Строит отчёт о влиянии без записи данных.</summary>
    Task<RecipeBaseParameterPreviewDto> PreviewBaseParametersAsync(RecipeBaseParameterBatchRequest request, CancellationToken ct = default);

    /// <summary>Проверяет подтверждённый отчёт и сохраняет весь пакет в одной транзакции.</summary>
    Task<RecipeBaseParameterSaveResult> SaveBaseParametersAsync(RecipeBaseParameterBatchRequest request, CancellationToken ct = default);
}