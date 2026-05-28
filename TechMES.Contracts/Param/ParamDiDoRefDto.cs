using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

public sealed class ParamDiDoRefDto
{
    public string EquipmentName { get; set; } = "";

    public string TypeName { get; set; } = "";

    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    public string? Description { get; set; }

    public string? Location { get; set; }

    public string? ValueText { get; set; }

    public double? NumericValue { get; set; }

    public bool? BooleanValue { get; set; }

    public bool ValueForced { get; set; }

    public bool ForceCmd { get; set; }

    public string? Chanel { get; set; }

    public string ChanelShort => FormatChanelShort(Chanel);

    public string Title =>
        string.IsNullOrWhiteSpace(Description)
            ? EquipmentName
            : $"{EquipmentName}: {Description}";

    private static string FormatChanelShort(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return "";

        var parts = raw.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : raw;
    }
}
