namespace TechMES.Contracts.Recipes;

/// <summary>
/// Шаблон стадии и количество привязанных к нему базовых параметров.
/// Code соответствует BaseRecipeStage.description и не является префиксом PLC-тегов.
/// </summary>
public sealed class RecipeStageTemplateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public int ParameterCount { get; set; }
    public string DisplayName => $"{Name} ({ParameterCount})";
}

/// <summary>
/// Шаблоны только того семейства, к которому относится выбранный рецепт.
/// </summary>
public sealed class RecipeStageTemplateCatalogDto
{
    public int BaseRecipeId { get; set; }
    public List<RecipeStageTemplateDto> Templates { get; set; } = [];
}

/// <summary>
/// Добавляет стадию в конец рецепта. Значения новых параметров инициализируются нулями.
/// </summary>
public sealed class RecipeStageAddRequest
{
    public int BaseStageId { get; set; }
    public string TagPrefix { get; set; } = "";
    public bool Auto { get; set; }
    public List<RecipeStagePositionDto> ExpectedStages { get; set; } = [];
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}