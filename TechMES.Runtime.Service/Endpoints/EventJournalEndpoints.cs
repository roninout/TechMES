using TechMES.Application.Events;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Отдаёт WEB снимок CDBEventJournal за выбранный включительный диапазон дат.
/// </summary>
public static class EventJournalEndpoints
{
    /// <summary>
    /// Проверяет диапазон и передаёт запрос провайдеру ODBC.
    /// </summary>
    public static IEndpointRouteBuilder MapEventJournalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events/journal", async (IEventJournalProvider provider, ILoggerFactory loggerFactory, DateOnly from, DateOnly to, bool? refresh, long? version, CancellationToken ct) =>
        {
            if (to < from || to == DateOnly.MaxValue)
                return Results.BadRequest("Invalid event journal date range.");

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
                loggerFactory.CreateLogger("EventJournalEndpoints").LogError(ex, "Failed to read CDBEventJournal from {From} to {To}.", from, to);
                return Results.Problem("Event journal could not be loaded from Plant SCADA.", statusCode: 503);
            }
        });

        return app;
    }
}