using TechMES.Contracts.Param;

namespace TechMES.Contracts.Recipes;

/// <summary>Запись одной ячейки PLC. Адрес тега и код стадии определяет Runtime.</summary>
public sealed class RecipePlcWriteRequest
{
    public int PageId { get; set; }
    public int StageId { get; set; }
    public int ColumnId { get; set; }
    public string LayoutStamp { get; set; } = "";
    public double ExpectedValue { get; set; }
    public ParamWriteRequest Write { get; set; } = new();
}