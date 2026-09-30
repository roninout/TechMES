namespace TechMES.Contracts.Alarms;

/// <summary>
/// Одна авария из текущей сводки Plant SCADA.
/// RawFields сохраняет исходные значения CtApi для диагностики.
/// </summary>
public sealed class ActiveAlarmDto
{
    public string Tag { get; init; } = "";
    public string Description { get; init; } = "";
    public string Area { get; init; } = "";
    public string Category { get; init; } = "";
    public string State { get; init; } = "";
    public DateTime? OccurredAt { get; init; }
    public string OccurredAtText { get; init; } = "";
    public string OnDate { get; init; } = "";
    public string AckDate { get; init; } = "";
    public IReadOnlyDictionary<string, string> RawFields { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Состояние опубликованного снимка и текущего фонового чтения.
/// При NotModified=true WEB сохраняет свой список Items.
/// </summary>
public sealed class ActiveAlarmsResponse
{
    public DateTimeOffset? LoadedAtUtc { get; init; }
    public IReadOnlyList<ActiveAlarmDto> Items { get; init; } = [];
    public bool Truncated { get; init; }
    public bool IsRefreshing { get; init; }
    public int ScannedCount { get; init; }
    public bool IsComplete { get; init; }
    public string? RefreshError { get; init; }
    public long Version { get; init; }
    public bool NotModified { get; init; }
}