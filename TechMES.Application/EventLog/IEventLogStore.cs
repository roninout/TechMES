using TechMES.Contracts.EventLog;

namespace TechMES.Application.EventLog;

/// <summary>
/// Application-интерфейс чтения EventLog данных из существующей EventPicker/PostgreSQL БД.
/// WEB использует его для Operation actions и Alarm history.
/// </summary>
public interface IEventLogStore
{
    /// <summary>
    /// Проверяет, доступна ли база EventPicker.
    /// </summary>
    Task<bool> CanConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Читает действия операторов за выбранный день с опциональным фильтром оборудования.
    /// </summary>
    Task<IReadOnlyList<OperatorActionDto>> GetOperatorActionsAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default);

    /// <summary>
    /// Читает историю тревог за выбранный день с опциональным фильтром оборудования.
    /// </summary>
    Task<IReadOnlyList<AlarmHistoryDto>> GetAlarmHistoryAsync(
        DateTime date,
        string? equipmentFilter,
        CancellationToken ct = default);
}
