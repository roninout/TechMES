namespace TechMES.Contracts.Param;

public sealed class ParamTrendPointDto
{
    public string Series { get; set; } = "";

    public DateTime Time { get; set; }

    public double Value { get; set; }

    public double RawValue { get; set; }

    public int Quality { get; set; }
}
