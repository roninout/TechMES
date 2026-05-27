using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

public sealed class ParamSnapshotResponse
{
    public string EquipmentName { get; set; } = "";

    public string TypeName { get; set; } = "";

    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public string? Unit { get; set; }

    public string? Location { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;

    public List<ParamPageKind> Pages { get; set; } = [];

    public List<ParamItemDto> Items { get; set; } = [];

    public List<ParamTrendItemDto> TrendItems { get; set; } = [];
}
