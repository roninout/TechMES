namespace TechMES.Contracts.Recipes;

/// <summary>Исходный снимок и отредактированный рецепт для атомарного сохранения.</summary>
public sealed class RecipeEditorSaveRequest
{
    public RecipeSnapshotDto Original { get; set; } = new();
    public RecipeSnapshotDto Value { get; set; } = new();
    public int? RecipePageId { get; set; }
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

/// <summary>Возвращает фактические ID новых стадий и сохранённые значения.</summary>
public sealed class RecipeEditorSaveResult
{
    public RecipeChangeStatus Status { get; set; }
    public RecipeSnapshotDto? Snapshot { get; set; }
    public string? Error { get; set; }
}