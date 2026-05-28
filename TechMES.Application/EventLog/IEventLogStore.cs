using TechMES.Contracts.EventLog;

namespace TechMES.Application.EventLog;

public interface IEventLogStore
{
    Task<bool> CanConnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OperatorActionDto>> GetOperatorActionsAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default);

    Task<IReadOnlyList<AlarmHistoryDto>> GetAlarmHistoryAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default);
}
