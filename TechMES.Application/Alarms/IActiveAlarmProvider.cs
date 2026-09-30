using TechMES.Contracts.Alarms;

namespace TechMES.Application.Alarms;

/// <summary>
/// Контракт снимка активных аварий для Runtime.Service.
/// knownVersion позволяет не передавать неизменившийся список повторно.
/// </summary>
public interface IActiveAlarmProvider
{
    Task<ActiveAlarmsResponse> GetActiveAsync(bool forceRefresh = false, long knownVersion = 0, CancellationToken ct = default);
}