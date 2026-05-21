namespace TechMES.Contracts.Info;

public sealed class EquipmentInfoDto
{
    public string EquipName { get; set; } = string.Empty;

    public string? ProductCode { get; set; }

    public string? Supplier { get; set; }

    public string? SupplierLogoFileName { get; set; }

    public string? SupplierLogoDataUrl { get; set; }

    public string? Description { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public int PhotoCount { get; set; }

    public int InstructionCount { get; set; }

    public int SchemeCount { get; set; }

    public List<EquipmentInfoNoteDto> Notes { get; set; } = [];
}
