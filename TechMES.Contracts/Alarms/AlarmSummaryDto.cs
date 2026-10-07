namespace TechMES.Contracts.Alarms;

/// <summary>
/// Запись истории из CDBAlarmSummary.
/// Priority берётся из CiAdvancedAlarm; время Runtime переводит из UTC в локальное.
/// </summary>
public sealed class AlarmSummaryDto
{
    public string RecordId { get; init; } = "";
    public int? Id { get; init; }
    public string Tag { get; init; } = "";
    public string Description { get; init; } = "";
    public string StateDesc { get; init; } = "";
    public string Severity { get; init; } = "";
    public int? SeverityValue { get; init; }
    public int? Priority { get; init; }
    public DateTime? ActiveAt { get; init; }
    public DateTime? InactiveAt { get; init; }
    public long? Duration { get; init; }
    public DateTime? AckAt { get; init; }
    public string AckUserName { get; init; } = "";
    public string ClientAddressDesc { get; init; } = "";
}

/// <summary>
/// Снимок истории за выбранный диапазон. Если NotModified=true, WEB оставляет у себя ранее полученный список.
/// </summary>
public sealed class AlarmSummaryResponse
{
    public IReadOnlyList<AlarmSummaryDto> Items { get; init; } = [];
    public DateTimeOffset? LoadedAtUtc { get; init; }
    public bool IsRefreshing { get; init; }
    public string? RefreshError { get; init; }
    public long Version { get; init; }
    public bool NotModified { get; init; }
    public long? ReadDurationMs { get; init; }
}