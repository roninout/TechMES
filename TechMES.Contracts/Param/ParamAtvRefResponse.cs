namespace TechMES.Contracts.Param;

/// <summary>
/// Ответ вкладки ATV reference page.
/// </summary>
public sealed class ParamAtvRefResponse
{
    /// <summary>
    /// Имя выбранного оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Поддерживается ли ATV reference page.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Диагностическое сообщение.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Время формирования ответа.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>
    /// Имя связанного ATV оборудования.
    /// </summary>
    public string? AtvEquipmentName { get; set; }

    /// <summary>
    /// Признак, что ATV найден через связь от Motor.
    /// </summary>
    public bool IsLinkedFromMotor { get; set; }

    /// <summary>
    /// Compact Param snapshot связанного ATV.
    /// </summary>
    public ParamLinkedParamDto? AtvEquipment { get; set; }
}
