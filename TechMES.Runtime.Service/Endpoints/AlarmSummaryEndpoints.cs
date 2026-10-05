using TechMES.Application.Alarms;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Предоставляет WEB историю аварий Plant SCADA за выбранный день.
/// </summary>
public static class AlarmSummaryEndpoints
{
    public static IEndpointRouteBuilder MapAlarmSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alarms/summary", async (IAlarmSummaryProvider provider, ILoggerFactory loggerFactory,
            DateOnly date, bool? refresh, long? version, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await provider.GetAsync(date, refresh == true, version.GetValueOrDefault(), ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AlarmSummaryEndpoints").LogError(ex, "Failed to read CDBAlarmSummary for {Date}.", date);
                return Results.Problem("Alarm history could not be loaded from Plant SCADA.", statusCode: 503);
            }
        });

        return app;
    }
}