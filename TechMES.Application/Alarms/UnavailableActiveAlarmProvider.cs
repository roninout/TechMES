using TechMES.Contracts.Alarms;

namespace TechMES.Application.Alarms;

/// <summary>
/// Явно сообщает, что в режимах Mock и Disabled нет источника аварий CtApi.
/// </summary>
public sealed class UnavailableActiveAlarmProvider : IActiveAlarmProvider
{
    public Task<ActiveAlarmsResponse> GetActiveAsync(bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Active alarms require a connected CtApi provider.");
    }
}