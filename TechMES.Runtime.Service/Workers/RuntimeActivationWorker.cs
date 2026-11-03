using Microsoft.Extensions.Options;
using TechMES.Application.Equipment;
using TechMES.Application.Messages;
using TechMES.Application.Scada;
using TechMES.Infrastructure.CtApi.Settings;
using TechMES.Infrastructure.PostgreSql;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Готовит рабочие зависимости после старта HTTP-службы.
/// В Standby рабочая инициализация не выполняется.
/// </summary>
public sealed class RuntimeActivationWorker(IServiceScopeFactory scopes, RuntimeActivityState activity, IOptions<RuntimeOptions> options, IOptions<CtApiOptions> healthOptions, IHostApplicationLifetime lifetime, ILogger<RuntimeActivationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopping = lifetime.ApplicationStopping.Register(() => activity.BeginDraining("Runtime is stopping."));

        if (options.Value.StartInStandby)
        {
            activity.EnterStandby("Configured to start in Standby.");
            logger.LogInformation("Runtime started in Standby. Business initialization is skipped.");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var activation = activity.BeginActivation();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, activation.CancellationToken);

                try
                {
                    using (var scope = scopes.CreateScope())
                    {
                        await scope.ServiceProvider.GetRequiredService<PostgreSqlDatabaseBootstrapper>().InitializeMainDatabaseAsync(stop.Token);
                        await scope.ServiceProvider.GetRequiredService<IMessageStore>().InitializeAsync(stop.Token);
                        await scope.ServiceProvider.GetRequiredService<IPlantScadaGateway>().InitializeAsync(stop.Token);
                        await scope.ServiceProvider.GetRequiredService<IEquipmentCatalogProvider>().InitializeAsync(stop.Token);
                    }

                    if (!activity.CompleteActivation(activation))
                        return;

                    logger.LogInformation("Runtime is Active. ActivationId={ActivationId}.", activation.ActivationId);

                    await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || activation.CancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    activity.MarkFaulted("Runtime dependencies are not ready. See Runtime logs.");
                    logger.LogError(ex, "Runtime initialization failed. Retrying without stopping the HTTP service.");
                }

                // Используем существующий Health Period, отдельной настройки не добавляем.
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, healthOptions.Value.HealthCheckPeriodSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException) when (activity.GetStatus().Mode == RuntimeActivityMode.Draining.ToString())
        {
        }
        finally
        {
            if (stoppingToken.IsCancellationRequested)
                activity.BeginDraining("Runtime is stopping.");
        }
    }
}