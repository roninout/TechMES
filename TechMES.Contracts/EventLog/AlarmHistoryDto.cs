namespace TechMES.Contracts.EventLog;

public sealed class AlarmHistoryDto
{
    public DateTime Timestamp { get; set; }

    public string Time { get; set; } = "";

    public string Category { get; set; } = "";

    public string User { get; set; } = "";

    public string Location { get; set; } = "";

    public string Equipment { get; set; } = "";

    public string Item { get; set; } = "";

    public string Comment { get; set; } = "";

    public string State { get; set; } = "";
}
