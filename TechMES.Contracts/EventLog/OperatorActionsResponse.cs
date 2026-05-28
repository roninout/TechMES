namespace TechMES.Contracts.EventLog;

public sealed class OperatorActionsResponse
{
    public DateTime Date { get; set; } = DateTime.Today;

    public string? EquipmentFilter { get; set; }

    public List<OperatorActionDto> Rows { get; set; } = [];
}
