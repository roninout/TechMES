namespace TechMES.Contracts.Info;

public sealed class EquipmentInfoSummaryDto
{
    public string EquipName { get; set; } = string.Empty;

    public int PhotoCount { get; set; }

    public int InstructionCount { get; set; }

    public int SchemeCount { get; set; }

    public int NoteCount { get; set; }
}
