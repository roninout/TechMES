namespace TechMES.Contracts.Alarms;

/// <summary>
/// Одна активная авария из CiAdvancedAlarm через ODBC.
/// RawFields сохраняет исходные значения полей для диагностики.
/// </summary>
public sealed class ActiveAlarmDto
{
    public string Tag { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public string State { get; init; } = "";
    public DateTime? OccurredAt { get; init; }
    public string OccurredAtText { get; init; } = "";
    public string OnDate { get; init; } = "";
    public string AckDate { get; init; } = "";
    public IReadOnlyDictionary<string, string> RawFields { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Состояние последнего завершённого снимка и текущего фонового чтения.
/// При NotModified=true WEB сохраняет свой список Items.
/// </summary>
public sealed class ActiveAlarmsResponse
{
    public DateTimeOffset? LoadedAtUtc { get; init; }
    public IReadOnlyList<ActiveAlarmDto> Items { get; init; } = [];
    public bool IsRefreshing { get; init; }
    public string? RefreshError { get; init; }
    public long Version { get; init; }
    public bool NotModified { get; init; }
    public long? ReadDurationMs { get; init; }
    public long? RefreshDurationMs { get; init; }
}