using Microsoft.Extensions.Options;
using TechMES.Application.Scada;
using TechMES.Infrastructure.CtApi.Odbc;
using TechMES.Infrastructure.CtApi.Settings;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Один Health Period для независимых проверок CtApi, ODBC и Main DB.
/// </summary>
public sealed class PlantScadaHealthWorker(IPlantScadaGateway gateway, IOptions<CtApiOptions> options, IEnumerable<AlarmOdbcFailover> odbcConnections, IEnumerable<MainDatabaseFailover> databases, ILogger<PlantScadaHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(5, options.Value.HealthCheckPeriodSeconds);
        var odbc = odbcConnections.SingleOrDefault();
        var database = databases.SingleOrDefault();
        var scadaCycle = Task.CompletedTask;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        logger.LogInformation("PlantScadaHealthWorker запущен. Provider={Provider}, Period={Seconds} sec.", options.Value.Provider, seconds);

        try
        {
            // Первые проверки запускаем сразу. Дальше все подсистемы используют один timer.
            do
            {
                odbc?.RefreshHealth();
                database?.RefreshHealth();

                // Долгая проверка CtApi не останавливает проверки ODBC и Main DB.
                if (scadaCycle.IsCompleted)
                    scadaCycle = CheckScadaAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await scadaCycle;
        }
    }

    private async Task CheckScadaAsync(CancellationToken ct)
    {
        try
        {
            if (gateway is IPlantScadaHealthMonitor monitor)
                await monitor.RefreshHealthAsync(ct);

            var health = await gateway.GetHealthAsync(ct);

            if (!health.IsConnected)
                logger.LogWarning("Plant SCADA status: {Status}. {Message}", health.Status, health.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка PlantScadaHealthWorker.");
        }
    }
}