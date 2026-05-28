namespace TechMES.Contracts.Param;

public sealed class ParamPlcRefDto
{
    public string EquipmentName { get; set; } = "";

    public string RefItem { get; set; } = "";

    public ParamPlcType Type { get; set; } = ParamPlcType.Unknown;

    public string? Comment { get; set; }

    public string? TagName { get; set; }

    public string? Unit { get; set; }

    public string? ValueText { get; set; }

    public double? NumericValue { get; set; }

    public bool? BooleanValue { get; set; }

    public bool? ForceCmd { get; set; }

    public string? ForcedTagName { get; set; }

    public string Title =>
        string.IsNullOrWhiteSpace(Comment)
            ? EquipmentName
            : $"{EquipmentName}: {Comment}";
}
