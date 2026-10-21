namespace TechMES.Contracts.Recipes;

/// <summary>Операции управления сохранёнными рецептами.</summary>
public enum RecipeChangeOperation
{
    Create = 1,
    Copy = 2,
    Rename = 3,
    Delete = 4,
    SetBase = 5
}

/// <summary>
/// SourceRecipeId задаёт исходный рецепт.
/// Ожидаемые данные защищают операции от изменений другим пользователем.
/// </summary>
public sealed class RecipeChangeRequest
{
    public RecipeChangeOperation Operation { get; set; }
    public int SourceRecipeId { get; set; }
    public string Name { get; set; } = "";
    public string? ExpectedName { get; set; }

    /// <summary>Исходный снимок рецепта перед удалением или назначением базовым.</summary>
    public RecipeSnapshotDto? ExpectedSnapshot { get; set; }

    /// <summary>Семейство и привязки Unit, показанные перед назначением базового рецепта.</summary>
    public int? ExpectedBaseRecipeId { get; set; }
    public List<RecipePageDto> ExpectedPages { get; set; } = [];

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

/// <summary>Идентификатор рецепта, с которым выполнена операция.</summary>
public sealed class RecipeChangeResult
{
    public RecipeChangeStatus Status { get; set; }
    public int? RecipeId { get; set; }
    public string? Error { get; set; }
}