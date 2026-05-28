namespace TechMES.Contracts.EventLog;

public sealed class AlarmHistoryResponse
{
    public DateTime Date { get; set; } = DateTime.Today;

    public string? EquipmentFilter { get; set; }

    public List<AlarmHistoryDto> Rows { get; set; } = [];
}
