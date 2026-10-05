using TechMES.Contracts.Alarms;

namespace TechMES.Application.Alarms;

/// <summary>
/// Читает историю CDBAlarmSummary за один выбранный календарный день.
/// </summary>
public interface IAlarmSummaryProvider
{
    Task<AlarmSummaryResponse> GetAsync(DateOnly date, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default);
}

/// <summary>
/// Поведение режимов Runtime без источника Plant SCADA.
/// </summary>
public sealed class UnavailableAlarmSummaryProvider : IAlarmSummaryProvider
{
    public Task<AlarmSummaryResponse> GetAsync(DateOnly date, bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Alarm summary requires a connected CtApi provider.");
    }
}