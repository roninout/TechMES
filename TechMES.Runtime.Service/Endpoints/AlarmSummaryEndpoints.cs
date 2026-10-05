using TechMES.Application.Alarms;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Передаёт WEB снимок истории за выбранный включительный диапазон дат.
/// </summary>
public static class AlarmSummaryEndpoints
{
    public static IEndpointRouteBuilder MapAlarmSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alarms/summary", async (IAlarmSummaryProvider provider, ILoggerFactory loggerFactory, DateOnly from, DateOnly to, bool? refresh, long? version, CancellationToken ct) =>
        {
            if (to < from || to == DateOnly.MaxValue)
                return Results.BadRequest("Invalid alarm history date range.");

            try
            {
                return Results.Ok(await provider.GetAsync(from, to, refresh == true, version.GetValueOrDefault(), ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AlarmSummaryEndpoints").LogError(ex, "Failed to read CDBAlarmSummary from {From} to {To}.", from, to);
                return Results.Problem("Alarm history could not be loaded from Plant SCADA.", statusCode: 503);
            }
        });

        return app;
    }
}