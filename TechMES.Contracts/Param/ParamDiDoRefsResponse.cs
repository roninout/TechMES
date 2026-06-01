namespace TechMES.Contracts.Param;

/// <summary>
/// Ответ вкладки DI/DO reference page.
/// </summary>
public sealed class ParamDiDoRefsResponse
{
    /// <summary>
    /// Имя выбранного оборудования.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Поддерживается ли DI/DO reference page.
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
    /// Связанные DI-элементы.
    /// </summary>
    public List<ParamDiDoRefDto> DiRows { get; set; } = [];

    /// <summary>
    /// Связанные DO-элементы.
    /// </summary>
    public List<ParamDiDoRefDto> DoRows { get; set; } = [];
}
