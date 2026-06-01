using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Ответ тренда Param-модуля для Apache ECharts.
/// </summary>
public sealed class ParamTrendResponse
{
    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// WEB-группа типа оборудования.
    /// </summary>
    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    /// <summary>
    /// Поддерживается ли trend для этого оборудования.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Диагностическое сообщение, если points нет или источник недоступен.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Начало диапазона в UTC.
    /// </summary>
    public DateTime FromUtc { get; set; }

    /// <summary>
    /// Конец диапазона в UTC.
    /// </summary>
    public DateTime ToUtc { get; set; }

    /// <summary>
    /// Минимум оси Y из MinR/настроек.
    /// </summary>
    public double? AxisYMin { get; set; }

    /// <summary>
    /// Максимум оси Y из MaxR/настроек.
    /// </summary>
    public double? AxisYMax { get; set; }

    /// <summary>
    /// Описание серий графика.
    /// </summary>
    public List<ParamTrendItemDto> Series { get; set; } = [];

    /// <summary>
    /// Точки тренда всех серий.
    /// </summary>
    public List<ParamTrendPointDto> Points { get; set; } = [];
}
