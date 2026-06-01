using TechMES.Application.EventLog;
using TechMES.Contracts.EventLog;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP API для Operation actions и Alarm history.
/// Данные читаются из существующей EventPicker/PostgreSQL базы, а не из новых WEB-таблиц.
/// </summary>
public static class EventLogEndpoints
{
    /// <summary>
    /// Подключает endpoints журналов операторских действий, истории тревог и проверки БД.
    /// </summary>
    public static IEndpointRouteBuilder MapEventLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/event-log/operator-actions", GetOperatorActionsAsync);
        app.MapGet("/api/event-log/alarm-history", GetAlarmHistoryAsync);
        app.MapGet("/api/event-log/health", GetHealthAsync);

        return app;
    }

    /// <summary>
    /// Возвращает действия операторов за выбранную дату с опциональным фильтром по оборудованию.
    /// </summary>
    private static async Task<IResult> GetOperatorActionsAsync(
        DateTime? date,
        string? equipmentFilter,
        IEventLogStore eventLogStore,
        CancellationToken ct)
    {
        var targetDate = ResolveDate(date);
        var rows = await eventLogStore.GetOperatorActionsAsync(targetDate, equipmentFilter, ct);

        return Results.Ok(new OperatorActionsResponse
        {
            Date = targetDate,
            EquipmentFilter = equipmentFilter,
            Rows = rows.ToList()
        });
    }

    /// <summary>
    /// Возвращает историю тревог за выбранную дату с опциональным фильтром по оборудованию.
    /// </summary>
    private static async Task<IResult> GetAlarmHistoryAsync(
        DateTime? date,
        string? equipmentFilter,
        IEventLogStore eventLogStore,
        CancellationToken ct)
    {
        var targetDate = ResolveDate(date);
        var rows = await eventLogStore.GetAlarmHistoryAsync(targetDate, equipmentFilter, ct);

        return Results.Ok(new AlarmHistoryResponse
        {
            Date = targetDate,
            EquipmentFilter = equipmentFilter,
            Rows = rows.ToList()
        });
    }

    /// <summary>
    /// Простая диагностика подключения к EventPicker/PostgreSQL.
    /// </summary>
    private static async Task<IResult> GetHealthAsync(
        IEventLogStore eventLogStore,
        CancellationToken ct)
    {
        return Results.Ok(new { Connected = await eventLogStore.CanConnectAsync(ct) });
    }

    /// <summary>
    /// Нормализует дату запроса. Если дата не передана, используем текущий день.
    /// </summary>
    private static DateTime ResolveDate(DateTime? date)
    {
        return (date ?? DateTime.Today).Date;
    }
}
