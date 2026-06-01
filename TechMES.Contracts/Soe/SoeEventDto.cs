using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Soe;

/// <summary>
/// Одна строка SOE, полученная из изменения битового trend-слова.
/// </summary>
public sealed class SoeEventDto
{
    /// <summary>
    /// UTC-время события из trend-истории.
    /// </summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>
    /// Локальное время для отображения в WEB.
    /// </summary>
    public DateTime TimeLocal => TimeUtc.ToLocalTime();

    /// <summary>
    /// Тип оборудования, определяющий расшифровку bit code.
    /// </summary>
    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    /// <summary>
    /// Имя оборудования или reference-оборудования, где произошло событие.
    /// </summary>
    public string Equipment { get; set; } = "";

    /// <summary>
    /// Сырое значение trend-слова.
    /// </summary>
    public double TrendValue { get; set; }

    /// <summary>
    /// Код изменившегося бита: 1..16 включение, 17..32 выключение.
    /// </summary>
    public long BitCode { get; set; }

    /// <summary>
    /// Человекочитаемый текст события.
    /// </summary>
    public string Event { get; set; } = "";

    /// <summary>
    /// Внутренний ключ события из enum-маппера.
    /// </summary>
    public string EventKey { get; set; } = "";

    /// <summary>
    /// Качество trend-значения, пришедшее из CtApi.
    /// </summary>
    public string ValueQuality { get; set; } = "";
}
