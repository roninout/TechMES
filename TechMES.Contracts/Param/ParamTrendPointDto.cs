namespace TechMES.Contracts.Param;

/// <summary>
/// Одна точка Param trend-а.
/// </summary>
public sealed class ParamTrendPointDto
{
    /// <summary>
    /// Имя серии, к которой относится точка.
    /// </summary>
    public string Series { get; set; } = "";

    /// <summary>
    /// Время точки в UTC.
    /// </summary>
    public DateTime Time { get; set; }

    /// <summary>
    /// Масштабированное значение для графика.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Сырое значение, пришедшее из CtApi.
    /// </summary>
    public double RawValue { get; set; }

    /// <summary>
    /// Код качества trend-точки.
    /// </summary>
    public int Quality { get; set; }
}
