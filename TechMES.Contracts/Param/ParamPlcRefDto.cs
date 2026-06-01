namespace TechMES.Contracts.Param;

/// <summary>
/// Одна строка PLC reference page из EquipRef.
/// </summary>
public sealed class ParamPlcRefDto
{
    /// <summary>
    /// Имя связанного оборудования или tag-контекста.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Имя reference item.
    /// </summary>
    public string RefItem { get; set; } = "";

    /// <summary>
    /// UI-тип строки, сохраненный в CUSTOM1.
    /// </summary>
    public ParamPlcType Type { get; set; } = ParamPlcType.Unknown;

    /// <summary>
    /// Комментарий/описание строки.
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Основной tag строки.
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Единица измерения.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Текстовое значение tag-а.
    /// </summary>
    public string? ValueText { get; set; }

    /// <summary>
    /// Числовое значение, если его удалось распарсить.
    /// </summary>
    public double? NumericValue { get; set; }

    /// <summary>
    /// Boolean-значение, если его удалось распарсить.
    /// </summary>
    public bool? BooleanValue { get; set; }

    /// <summary>
    /// Признак активной команды force.
    /// </summary>
    public bool? ForceCmd { get; set; }

    /// <summary>
    /// Tag forced-состояния, если он есть.
    /// </summary>
    public string? ForcedTagName { get; set; }

    /// <summary>
    /// Заголовок строки для UI.
    /// </summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Comment)
            ? EquipmentName
            : $"{EquipmentName}: {Comment}";
}
