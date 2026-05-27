namespace TechMES.Contracts.Param;

public sealed class ParamItemDto
{
    public string Name { get; set; } = "";

    public ParamValueKind Kind { get; set; } = ParamValueKind.Unknown;

    public string? ValueText { get; set; }

    public double? NumericValue { get; set; }

    public bool? BooleanValue { get; set; }

    public string? Unit { get; set; }

    public string? TagName { get; set; }
}
