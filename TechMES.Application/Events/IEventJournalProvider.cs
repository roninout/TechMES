using TechMES.Contracts.Events;

namespace TechMES.Application.Events;

/// <summary>
/// Читает CDBEventJournal за включительный диапазон локальных дат.
/// </summary>
public interface IEventJournalProvider
{
    Task<EventJournalResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default);
}

/// <summary>
/// Явно сообщает об отсутствии источника событий в режимах Mock и Disabled.
/// </summary>
public sealed class UnavailableEventJournalProvider : IEventJournalProvider
{
    public Task<EventJournalResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Event journal requires a connected CtApi provider.");
    }
}