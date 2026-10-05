using TechMES.Contracts.Alarms;

namespace TechMES.Application.Alarms;

/// <summary>
/// Читает историю CDBAlarmSummary за включительный диапазон локальных дат.
/// </summary>
public interface IAlarmSummaryProvider
{
    Task<AlarmSummaryResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default);
}

/// <summary>
/// Ответ для режима Runtime без источника Plant SCADA.
/// </summary>
public sealed class UnavailableAlarmSummaryProvider : IAlarmSummaryProvider
{
    public Task<AlarmSummaryResponse> GetAsync(DateOnly from, DateOnly to, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Alarm summary requires a connected CtApi provider.");
    }
}