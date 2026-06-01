namespace TechMES.Contracts.Param;

/// <summary>
/// Описание одной серии Param trend-а.
/// </summary>
public sealed class ParamTrendItemDto
{
    /// <summary>
    /// Имя серии, обычно короткий item вроде R.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Цвет серии для графика.
    /// </summary>
    public string Color { get; set; } = "#4F81BD";

    /// <summary>
    /// Нижняя граница шкалы из SCADA/Param.
    /// </summary>
    public double? NativeMin { get; set; }

    /// <summary>
    /// Верхняя граница шкалы из SCADA/Param.
    /// </summary>
    public double? NativeMax { get; set; }
}
