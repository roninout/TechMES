using TechMES.Application.Alarms;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP-доступ к снимку аварий Plant SCADA.
/// </summary>
public static class ActiveAlarmEndpoints
{
    /// <summary>
    /// Подключает endpoint чтения. Подробная ошибка остаётся в журнале Runtime,
    /// а WEB получает безопасное сообщение и HTTP 503.
    /// </summary>
    public static IEndpointRouteBuilder MapActiveAlarmEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alarms/active", async (IActiveAlarmProvider provider, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await provider.GetActiveAsync(ct));
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