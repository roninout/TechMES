namespace TechMES.Contracts.Recipes;

/// <summary>Состав и раскладка шаблона: 4 столбца, 8 строк; null означает пустую ячейку.</summary>
public sealed class RecipeBaseStageDto
{
    public int Id { get; set; }
    public int FamilyId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public List<int?> Slots { get; set; } = Enumerable.Repeat<int?>(null, 32).ToList();
    public List<int> Overflow { get; set; } = [];
    public int ParameterCount => Slots.Count(id => id.HasValue) + Overflow.Count;
}

public sealed class RecipeBaseStageRequest
{
    public int FamilyId { get; set; }
    public RecipeBaseStageDto? Original { get; set; }
    public RecipeBaseStageDto Value { get; set; } = new();
    public string? PreviewToken { get; set; }
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

public sealed class RecipeStageSyncImpactDto
{
    public int RecipeId { get; set; }
    public string RecipeName { get; set; } = "";
    public int Stages { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Preserved { get; set; }
}

public sealed class RecipeBaseStagePreviewDto
{
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Changes { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<RecipeStageSyncImpactDto> Recipes { get; set; } = [];
    public bool CanSave => Errors.Count == 0 && Name.Length > 0;
}

public sealed class RecipeBaseStageSaveResult
{
    public RecipeChangeStatus Status { get; set; }
    public int StageId { get; set; }
    public string? Error { get; set; }
}