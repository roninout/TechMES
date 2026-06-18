namespace TechMES.Contracts.Param;

/// <summary>
/// Ответ вкладки PLC reference page.
/// Эта вкладка строится из Plant SCADA EquipRef category=TabPLC и может использоваться
/// разными типами оборудования: Motor, VGA_EL, VGD и будущими Param-моделями.
/// </summary>
public sealed class ParamPlcRefsResponse
{
    /// <summary>
    /// Имя оборудования, для которого была запрошена PLC reference page.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Поддерживается ли PLC reference page для выбранного оборудования.
    /// false означает, что строк TabPLC нет или провайдер недоступен.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Диагностическое сообщение для UI.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Время формирования ответа Runtime.Service.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>
    /// PLC reference rows, уже дополненные текущими значениями tag-ов.
    /// </summary>
    public List<ParamPlcRefDto> Rows { get; set; } = [];
}
