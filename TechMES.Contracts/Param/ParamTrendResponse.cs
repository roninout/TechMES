using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

public sealed class ParamTrendResponse
{
    public string EquipmentName { get; set; } = "";

    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public DateTime FromUtc { get; set; }

    public DateTime ToUtc { get; set; }

    public double? AxisYMin { get; set; }

    public double? AxisYMax { get; set; }

    public List<ParamTrendItemDto> Series { get; set; } = [];

    public List<ParamTrendPointDto> Points { get; set; } = [];
}
