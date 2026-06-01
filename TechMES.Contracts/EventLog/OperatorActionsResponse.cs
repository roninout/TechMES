namespace TechMES.Contracts.EventLog;

/// <summary>
/// Ответ endpoint-а Operation actions.
/// </summary>
public sealed class OperatorActionsResponse
{
    /// <summary>
    /// Локальная дата, за которую загружались действия.
    /// </summary>
    public DateTime Date { get; set; } = DateTime.Today;

    /// <summary>
    /// Фильтр оборудования, примененный к запросу.
    /// </summary>
    public string? EquipmentFilter { get; set; }

    /// <summary>
    /// Строки действий операторов.
    /// </summary>
    public List<OperatorActionDto> Rows { get; set; } = [];
}
