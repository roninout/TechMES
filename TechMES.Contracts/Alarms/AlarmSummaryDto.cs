namespace TechMES.Contracts.Alarms;

/// <summary>
/// Запись истории аварий из CDBAlarmSummary.
/// Время уже переведено Runtime Service из UTC в локальное время сервера.
/// </summary>
public sealed class AlarmSummaryDto
{
    public string RecordId { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Description { get; init; } = "";
    public string Severity { get; init; } = "";
    public int? SeverityValue { get; init; }
    public DateTime? ActiveAt { get; init; }
}

/// <summary>
/// Состояние чтения выбранного дня. WEB получает список только при смене Version.
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