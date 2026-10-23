namespace TechMES.Contracts.Recipes;

/// <summary>Сохраняет PLC-снимок поверх ожидаемых значений БД, не меняя структуру рецепта.</summary>
public sealed class RecipeValuesSaveRequest
{
    public RecipeSnapshotDto Original { get; set; } = new();
    public List<RecipeValueSaveItem> Values { get; set; } = [];
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

/// <summary>Адрес ячейки задаётся ID стадии и базового параметра, а не именем тега.</summary>
public sealed class RecipeValueSaveItem
{
    public int StageId { get; set; }
    public int ColumnId { get; set; }
    public double Value { get; set; }
}