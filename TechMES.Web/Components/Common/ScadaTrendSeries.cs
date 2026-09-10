namespace TechMES.Web.Components.Common;

public enum ScadaTrendStyle { Line, Area }

// Настройки серии задаются вызывающей страницей, а не пользователем графика.
public sealed record ScadaTrendSeries
{
    public string TagName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Color { get; init; } = "";
    public string Unit { get; init; } = "";
    public ScadaTrendStyle Style { get; init; } = ScadaTrendStyle.Line;
    public double StrokeWidth { get; init; } = 2;
    public bool? TrendAvailable { get; init; }
}