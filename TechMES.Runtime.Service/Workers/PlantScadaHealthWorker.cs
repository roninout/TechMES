using Microsoft.Extensions.Options;
using TechMES.Application.Scada;
using TechMES.Infrastructure.CtApi.Odbc;
using TechMES.Infrastructure.CtApi.Settings;
using TechMES.Infrastructure.PostgreSql.Connections;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.PostgreSql;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Один Health Period для независимых проверок CtApi, ODBC, Main DB и кластера PostgreSQL.
/// </summary>
public sealed class PlantScadaHealthWorker(IPlantScadaGateway gateway, IOptions<CtApiOptions> options, IEnumerable<AlarmOdbcFailover> odbcConnections, IEnumerable<MainDatabaseFailover> databases, PostgreSqlClusterMonitor cluster, PostgreSqlLocalMonitor localDatabase, RuntimeActivityState activity, ILogger<PlantScadaHealthWorker> logger) : BackgroundService
{
    /// <summary>
    /// Запускает независимые проверки по одному существующему таймеру. Монитор PostgreSQL работает также в Standby; при остановке ожидаем завершения текущих циклов.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(5, options.Value.HealthCheckPeriodSeconds);
        var odbc = odbcConnections.SingleOrDefault();
        var database = databases.SingleOrDefault();
        var scadaCycle = Task.CompletedTask;
        var clusterCycle = Task.CompletedTask;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        logger.LogInformation("PlantScadaHealthWorker запущен. Provider={Provider}, Period={Seconds} sec.", options.Value.Provider, seconds);

        try
        {
            do
            {
                odbc?.RefreshHealth();
                database?.RefreshHealth();

                // Проверка кластера работает также в Standby.
                // Новый таймер не создаём; медленная проверка не задерживает CtApi/ODBC.
                if (clusterCycle.IsCompleted)
                    clusterCycle = CheckClusterAsync(stoppingToken);

                if (activity.GetStatus().CanExecuteCommands && scadaCycle.IsCompleted)
                    scadaCycle = CheckScadaAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(scadaCycle, clusterCycle);
        }
    }

    /// <summary>
    /// Обновляет диагностику PostgreSQL вне разрешения на бизнес-операции. Ошибка проверки не завершает health worker.
    /// </summary>
    private async Task CheckClusterAsync(CancellationToken ct)
    {
        try
        {
            // SQL-кластер и локальная служба проверяются независимо, одним health worker.
            await Task.WhenAll(cluster.RefreshAsync(ct), localDatabase.RefreshAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("PostgreSQL cluster monitor failed. ErrorType={ErrorType}.", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Проверяет SCADA только в действующей сессии Active. При утрате активности отменяет запрос и освобождает разрешение.
    /// </summary>
    private async Task CheckScadaAsync(CancellationToken ct)
    {
        try
        {
            using var lease = activity.Acquire();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

            if (gateway is IPlantScadaHealthMonitor monitor)
                await monitor.RefreshHealthAsync(stop.Token);

            var health = await gateway.GetHealthAsync(stop.Token);

            if (!health.IsConnected)
                logger.LogWarning("Plant SCADA status: {Status}. {Message}", health.Status, health.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RuntimeNotActiveException)
        {
        }
        catch (OperationCanceledException) when (!activity.GetStatus().CanExecuteCommands)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка PlantScadaHealthWorker.");
        }
    }
}