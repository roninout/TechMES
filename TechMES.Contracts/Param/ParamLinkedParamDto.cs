using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Compact read-only Param payload for equipment linked from another Param page.
/// </summary>
public sealed class ParamLinkedParamDto
{
    public string EquipmentName { get; set; } = "";

    public string TypeName { get; set; } = "";

    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    public string? Description { get; set; }

    public string? Location { get; set; }

    public string? Unit { get; set; }

    public List<ParamItemDto> Items { get; set; } = [];

    public string Title =>
        string.IsNullOrWhiteSpace(Description)
            ? EquipmentName
            : $"{EquipmentName}: {Description}";
}
