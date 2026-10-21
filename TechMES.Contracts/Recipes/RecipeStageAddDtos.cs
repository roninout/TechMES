namespace TechMES.Contracts.Recipes;

/// <summary>
/// Шаблон стадии семейства.
/// Code соответствует BaseRecipeStage.description и не является PLC-prefix.
/// </summary>
public sealed class RecipeStageTemplateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public int ParameterCount { get; set; }
    public string DisplayName => $"{Name} ({ParameterCount})";
}

/// <summary>Шаблоны семейства выбранного рецепта.</summary>
public sealed class RecipeStageTemplateCatalogDto
{
    public int BaseRecipeId { get; set; }
    public List<RecipeStageTemplateDto> Templates { get; set; } = [];
}

/// <summary>Добавляет стадию перед указанной строкой или в конец рецепта.</summary>
public sealed class RecipeStageAddRequest
{
    public int BaseStageId { get; set; }
    public string TagPrefix { get; set; } = "";
    public bool Auto { get; set; }

    /// <summary>Сервер определяет prefix по данным рецепта и выбранного Unit.</summary>
    public bool AutoAssignTagPrefix { get; set; }

    public int? RecipePageId { get; set; }

    /// <summary>Если строка не указана, стадия добавляется в конец.</summary>
    public int? InsertBeforeStageId { get; set; }

    public List<RecipeStagePositionDto> ExpectedStages { get; set; } = [];
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}