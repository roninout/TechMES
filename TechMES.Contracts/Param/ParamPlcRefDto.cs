namespace TechMES.Contracts.Param;

/// <summary>
/// Одна строка PLC reference page из EquipRef category=TabPLC.
/// Runtime.Service читает RЕFEQUIP, REFITEM, CUSTOM1 и COMMENT, а затем дополняет строку
/// текущим значением тега и служебной информацией для отображения в WEB.
/// </summary>
public sealed class ParamPlcRefDto
{
    /// <summary>
    /// Имя связанного оборудования или tag-контекста из REFEQUIP.
    /// Именно в это оборудование выполняется переход из PLC строки.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Имя reference item из REFITEM.
    /// Если значение пустое, backend использует WPF fallback: State для status rows, Value для остальных.
    /// </summary>
    public string RefItem { get; set; } = "";

    /// <summary>
    /// UI-тип строки из CUSTOM1. Он выбирает шаблон отображения и, где разрешено, тип write-dialog.
    /// </summary>
    public ParamPlcType Type { get; set; } = ParamPlcType.Unknown;

    /// <summary>
    /// Комментарий/описание строки из COMMENT.
    /// В UI отображается рядом с именем оборудования.
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Основной tag строки, разрешенный через TagInfo(equipment.item, 0).
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Единица измерения основного tag, если CtApi смог ее вернуть.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Текстовое значение tag-а в том виде, который безопасно показать в UI.
    /// </summary>
    public string? ValueText { get; set; }

    /// <summary>
    /// Числовое представление значения, если Runtime.Service смог разобрать его как number.
    /// </summary>
    public double? NumericValue { get; set; }

    /// <summary>
    /// Boolean-представление значения, если Runtime.Service смог разобрать его как on/off.
    /// </summary>
    public bool? BooleanValue { get; set; }

    /// <summary>
    /// Признак активной команды форсирования.
    /// Сейчас используется для EqDigital/EqDigitalInOut, чтобы показать мигающий Forced.
    /// </summary>
    public bool? ForceCmd { get; set; }

    /// <summary>
    /// Tag forced-состояния, если для строки есть отдельный ForceCmd tag.
    /// </summary>
    public string? ForcedTagName { get; set; }

    /// <summary>
    /// Заголовок строки для UI и tooltip.
    /// </summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Comment)
            ? EquipmentName
            : $"{EquipmentName}: {Comment}";
}
