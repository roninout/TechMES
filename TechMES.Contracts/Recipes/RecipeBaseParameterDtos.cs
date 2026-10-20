namespace TechMES.Contracts.Recipes;

public sealed record RecipeFamilyDto(int Id, string Name);

/// <summary>Метаданные параметра; значения в рецептах хранятся отдельно.</summary>
public sealed record RecipeBaseParameterDto
{
    public int Id { get; set; }
    public int? BaseRecipeId { get; set; }
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tag { get; set; } = "";
    public RecipeParameterKind Kind { get; set; }
    public RecipeParameterFormat Format { get; set; }
}

public sealed class RecipeBaseParameterCatalogDto
{
    public List<RecipeFamilyDto> Families { get; set; } = [];
    public List<RecipeBaseParameterDto> Parameters { get; set; } = [];
}

/// <summary>Отрицательный Value.Id — временный ID нового параметра на странице.</summary>
public sealed record RecipeBaseParameterChangeDto
{
    public RecipeBaseParameterDto? Original { get; set; }
    public RecipeBaseParameterDto Value { get; set; } = new();
}

public sealed class RecipeBaseParameterBatchRequest
{
    public List<RecipeBaseParameterChangeDto> Changes { get; set; } = [];
    public string? PreviewToken { get; set; }
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

public sealed class RecipeBaseParameterImpactDto
{
    public string Name { get; set; } = "";
    public string Action { get; set; } = "";
    public string Family { get; set; } = "";
    public List<string> Fields { get; set; } = [];
    public List<string> Stages { get; set; } = [];
    public List<string> Recipes { get; set; } = [];
    public int ValuesCount { get; set; }
}

public sealed class RecipeBaseParameterPreviewDto
{
    public string Token { get; set; } = "";
    public List<RecipeBaseParameterImpactDto> Changes { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool CanSave => Changes.Count > 0 && Errors.Count == 0;
}

public sealed class RecipeBaseParameterSaveResult
{
    public RecipeChangeStatus Status { get; set; }
    public int ChangedParameters { get; set; }
    public int SynchronizedValues { get; set; }
    public string? Error { get; set; }
}