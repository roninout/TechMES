using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Текущий снимок Param-модуля для выбранного оборудования.
/// </summary>
public sealed class ParamSnapshotResponse
{
    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Полный SCADA type.
    /// </summary>
    public string TypeName { get; set; } = "";

    /// <summary>
    /// WEB-группа типа оборудования.
    /// </summary>
    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    /// <summary>
    /// Поддерживается ли Param для этого оборудования.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Диагностическое сообщение для UI.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Основная единица измерения, если она применима.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Location из SCADA/каталога.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Время формирования snapshot-а.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>
    /// Доступные вкладки Param для текущего типа оборудования.
    /// </summary>
    public List<ParamPageKind> Pages { get; set; } = [];

    /// <summary>
    /// Текущие значения Param items.
    /// </summary>
    public List<ParamItemDto> Items { get; set; } = [];

    /// <summary>
    /// Описание серий, которые можно построить на графике.
    /// </summary>
    public List<ParamTrendItemDto> TrendItems { get; set; } = [];
}
