using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Soe;

public sealed class SoeEventDto
{
    public DateTime TimeUtc { get; set; }

    public DateTime TimeLocal => TimeUtc.ToLocalTime();

    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    public string Equipment { get; set; } = "";

    public double TrendValue { get; set; }

    public long BitCode { get; set; }

    public string Event { get; set; } = "";

    public string EventKey { get; set; } = "";

    public string ValueQuality { get; set; } = "";
}
