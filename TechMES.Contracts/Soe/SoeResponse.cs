namespace TechMES.Contracts.Soe;

/// <summary>
/// Ответ SOE endpoint-а для выбранного оборудования.
/// </summary>
public sealed class SoeResponse
{
    /// <summary>
    /// Имя оборудования, для которого запрошен SOE.
    /// </summary>
    public string EquipmentName { get; set; } = "";

    /// <summary>
    /// Поддерживается ли SOE для выбранного типа/источника данных.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// Диагностическое сообщение, если событий нет или модуль недоступен.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Количество загруженных строк после ограничения totalMax.
    /// </summary>
    public int TotalLoaded { get; set; }

    /// <summary>
    /// Время формирования ответа Runtime.Service.
    /// </summary>
    public DateTime LoadedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Список SOE-событий.
    /// </summary>
    public List<SoeEventDto> Rows { get; set; } = [];
}
