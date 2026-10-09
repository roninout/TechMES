namespace TechMES.Contracts.Events;

/// <summary>
/// Запись журнала событий CDBEventJournal. Date содержит локальное время Runtime.
/// </summary>
public sealed class EventJournalDto
{
    public DateTime? Date { get; init; }
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public string ClientAddressDesc { get; init; } = "";
    public string User { get; init; } = "";
    public int? Id { get; init; }
    public string AlarmStateDesc { get; init; } = "";
}

/// <summary>
/// Снимок событий выбранного периода. Версия позволяет WEB не получать повторно неизменённую таблицу.
/// </summary>
public sealed class EventJournalResponse
{
    public IReadOnlyList<EventJournalDto> Items { get; init; } = [];
    public DateTimeOffset? LoadedAtUtc { get; init; }
    public bool IsRefreshing { get; init; }
    public string? RefreshError { get; init; }
    public long Version { get; init; }
    public bool NotModified { get; init; }
    public long? ReadDurationMs { get; init; }
}