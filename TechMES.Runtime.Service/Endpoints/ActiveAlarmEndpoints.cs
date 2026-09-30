using TechMES.Application.Alarms;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP-доступ к снимку аварий Plant SCADA.
/// </summary>
public static class ActiveAlarmEndpoints
{
    /// <summary>
    /// Отдаёт кэшированный снимок. refresh=true запускает обновление, а version
    /// позволяет не передавать строки, которые WEB уже получил.
    /// </summary>
    public static IEndpointRouteBuilder MapActiveAlarmEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alarms/active", async (IActiveAlarmProvider provider, ILoggerFactory loggerFactory, bool? refresh, long? version, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await provider.GetActiveAsync(refresh == true, version.GetValueOrDefault(), ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("ActiveAlarmEndpoints").LogError(ex, "Failed to read active alarms from Plant SCADA.");
                return Results.Problem("Active alarms could not be loaded from Plant SCADA.", statusCode: 503);
            }
        });

        return app;
    }
}