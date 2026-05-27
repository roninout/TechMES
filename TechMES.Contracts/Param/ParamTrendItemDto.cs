namespace TechMES.Contracts.Param;

public sealed class ParamTrendItemDto
{
    public string Name { get; set; } = "";

    public string Color { get; set; } = "#4F81BD";

    public double? NativeMin { get; set; }

    public double? NativeMax { get; set; }
}
