namespace TechMES.Contracts.EventLog;

/// <summary>
/// Ответ endpoint-а Alarm history.
/// </summary>
public sealed class AlarmHistoryResponse
{
    /// <summary>
    /// Локальная дата, за которую загружалась история тревог.
    /// </summary>
    public DateTime Date { get; set; } = DateTime.Today;

    /// <summary>
    /// Фильтр оборудования, примененный к запросу.
    /// </summary>
    public string? EquipmentFilter { get; set; }

    /// <summary>
    /// Строки истории тревог.
    /// </summary>
    public List<AlarmHistoryDto> Rows { get; set; } = [];
}
