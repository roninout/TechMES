using TechMES.Contracts.Alarms;

namespace TechMES.Application.Alarms;

/// <summary>
/// Явно сообщает об отсутствии аварийного источника в режимах Mock и Disabled.
/// </summary>
public sealed class UnavailableActiveAlarmProvider : IActiveAlarmProvider
{
    public Task<ActiveAlarmsResponse> GetActiveAsync(CancellationToken ct = default)
    {
        throw new InvalidOperationException("Active alarms require a connected CtApi provider.");
    }
}