namespace TechMES.Contracts.Recipes;

/// <summary>Операции управления сохранёнными рецептами.</summary>
public enum RecipeChangeOperation
{
    Create = 1,
    Copy = 2,
    Rename = 3,
    Delete = 4
}

/// <summary>
/// SourceRecipeId задаёт исходный рецепт и его семейство.
/// ExpectedName защищает переименование от чужого изменения имени.
/// </summary>
public sealed class RecipeChangeRequest
{
    public RecipeChangeOperation Operation { get; set; }
    public int SourceRecipeId { get; set; }
    public string Name { get; set; } = "";
    public string? ExpectedName { get; set; }

    /// <summary>Исходный снимок защищает удаление от изменений другим пользователем.</summary>
    public RecipeSnapshotDto? ExpectedSnapshot { get; set; }

    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

/// <summary>Результат операции без привязки к HTTP.</summary>
public enum RecipeChangeStatus
{
    Saved = 1,
    NotFound = 2,
    Conflict = 3,
    Invalid = 4
}

/// <summary>
/// RecipeId — ID созданного, скопированного, переименованного или удалённого рецепта.
/// </summary>
public sealed class RecipeChangeResult
{
    public RecipeChangeStatus Status { get; set; }
    public int? RecipeId { get; set; }
    public string? Error { get; set; }
}