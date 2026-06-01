namespace TechMES.Contracts.Param;

/// <summary>
/// Ответ вкладки PLC reference page.
/// </summary>
public sealed class ParamPlcRefsResponse
{
    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Поддерживается ли PLC reference page.
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
    /// PLC reference rows.
    /// </summary>
    public List<ParamPlcRefDto> Rows { get; set; } = [];
}
