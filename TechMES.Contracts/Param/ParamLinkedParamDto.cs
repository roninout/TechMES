using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Компактный read-only Param payload для оборудования, связанного с другой Param-вкладки.
/// </summary>
public sealed class ParamLinkedParamDto
{
    /// <summary>
    /// Имя связанного оборудования.
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
    /// Описание связанного оборудования.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Location/channel.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Основная единица измерения.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Текущие значения item-ов.
    /// </summary>
    public List<ParamItemDto> Items { get; set; } = [];

    /// <summary>
    /// Заголовок для UI.
    /// </summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Description)
            ? EquipmentName
            : $"{EquipmentName}: {Description}";
}
