using TechMES.Application.EventLog;
using TechMES.Contracts.EventLog;

namespace TechMES.Runtime.Service.Endpoints;

public static class EventLogEndpoints
{
    public static IEndpointRouteBuilder MapEventLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/event-log/operator-actions", GetOperatorActionsAsync);
        app.MapGet("/api/event-log/alarm-history", GetAlarmHistoryAsync);
        app.MapGet("/api/event-log/health", GetHealthAsync);

        return app;
    }

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

    private static async Task<IResult> GetHealthAsync(
        IEventLogStore eventLogStore,
        CancellationToken ct)
    {
        return Results.Ok(new { Connected = await eventLogStore.CanConnectAsync(ct) });
    }

    private static DateTime ResolveDate(DateTime? date)
    {
        return (date ?? DateTime.Today).Date;
    }
}
