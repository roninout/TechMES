using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Одна DI/DO reference-строка с текущим значением и force-флагами.
/// </summary>
public sealed class ParamDiDoRefDto
{
    /// <summary>
    /// Имя связанного DI/DO оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Полный SCADA type.
    /// </summary>
    public string TypeName { get; set; } = "";

    /// <summary>
    /// WEB-группа типа.
    /// </summary>
    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    /// <summary>
    /// Описание элемента.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Location/channel из SCADA.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Текстовое значение.
    /// </summary>
    public string? ValueText { get; set; }

    /// <summary>
    /// Числовое значение, если применимо.
    /// </summary>
    public double? NumericValue { get; set; }

    /// <summary>
    /// Boolean-значение, если применимо.
    /// </summary>
    public bool? BooleanValue { get; set; }

    /// <summary>
    /// Признак принудительно заданного значения.
    /// </summary>
    public bool ValueForced { get; set; }

    /// <summary>
    /// Команда force включена.
    /// </summary>
    public bool ForceCmd { get; set; }

    /// <summary>
    /// Полный channel/location.
    /// </summary>
    public string? Chanel { get; set; }

    /// <summary>
    /// Сокращенный channel для компактного отображения.
    /// </summary>
    public string ChanelShort => FormatChanelShort(Chanel);

    /// <summary>
    /// Заголовок строки.
    /// </summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Description)
            ? EquipmentName
            : $"{EquipmentName}: {Description}";

    /// <summary>
    /// Сокращает channel до первых двух сегментов.
    /// </summary>
    private static string FormatChanelShort(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return "";

        var parts = raw.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : raw;
    }
}
