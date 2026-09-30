using TechMES.Contracts.Alarms;

namespace TechMES.Application.Alarms;

/// <summary>
/// Контракт получения снимка аварий. Runtime.Service не зависит от CtApi.dll.
/// </summary>
public interface IActiveAlarmProvider
{
    Task<ActiveAlarmsResponse> GetActiveAsync(CancellationToken ct = default);
}