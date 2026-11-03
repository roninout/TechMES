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
/// Поддерживает повторяемый жизненный цикл Runtime без перезапуска HTTP-службы.
/// Межкомпьютерный выбор активного узла добавим поверх этого механизма.
/// </summary>
public sealed class RuntimeActivationWorker(IServiceScopeFactory scopes, RuntimeActivityState activity, IOptionsMonitor<RuntimeOptions> options, IOptions<CtApiOptions> healthOptions, IHostApplicationLifetime lifetime, ILogger<RuntimeActivationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var changes = options.OnChange((settings, _) => activity.SetStandbyRequested(settings.StartInStandby));
        using var stopping = lifetime.ApplicationStopping.Register(() => activity.BeginDraining("Runtime is stopping."));

        activity.SetStandbyRequested(options.CurrentValue.StartInStandby);

        // Используем существующий Health Period для повторной подготовки и проверки режима.
        var period = TimeSpan.FromSeconds(Math.Max(5, healthOptions.Value.HealthCheckPeriodSeconds));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var state = activity.GetStatus();

                if (state.StandbyRequested)
                {
                    if (state.Mode != RuntimeActivityMode.Standby.ToString())
                        await EnterStandbyAsync("Runtime is held in Standby by configuration.", stoppingToken);
                }
                else if (!state.CanExecuteCommands)
                {
                    // Faulted/Draining могут ещё удерживать отменённые операции старой эпохи.
                    await activity.WaitForIdleAsync(stoppingToken);
                    stoppingToken.ThrowIfCancellationRequested();

                    if (activity.GetStatus().Mode == RuntimeActivityMode.Draining.ToString())
                        activity.EnterStandby("Previous Runtime operations have finished.");

                    await ActivateAsync(stoppingToken);
                }

                await Task.Delay(period, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            activity.BeginDraining("Runtime is stopping.");
        }
    }

    /// <summary>
    /// Освобождает разрешение подготовки до ожидания завершения остальных операций.
    /// Иначе worker ожидал бы собственный lease и никогда не завершил Draining.
    /// </summary>
    private async Task ActivateAsync(CancellationToken stoppingToken)
    {
        using var activation = activity.TryBeginActivation();

        if (activation is null)
            return;

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, activation.CancellationToken))
        {
            try
            {
                using (var scope = scopes.CreateScope())
                {
                    await scope.ServiceProvider.GetRequiredService<PostgreSqlDatabaseBootstrapper>().InitializeMainDatabaseAsync(stop.Token);
                    await scope.ServiceProvider.GetRequiredService<IMessageStore>().InitializeAsync(stop.Token);
                    await scope.ServiceProvider.GetRequiredService<IPlantScadaGateway>().InitializeAsync(stop.Token);
                    await scope.ServiceProvider.GetRequiredService<IEquipmentCatalogProvider>().InitializeAsync(stop.Token);
                }

                stop.Token.ThrowIfCancellationRequested();

                if (activity.CompleteActivation(activation))
                    logger.LogInformation("Runtime is Active. ActivationId={ActivationId}.", activation.ActivationId);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Потеря разрешения подготовки не является ошибкой зависимостей.
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                // Некоторые адаптеры оборачивают отмену в собственное исключение.
            }
            catch (Exception ex)
            {
                activity.MarkFaulted("Runtime dependencies are not ready. See Runtime logs.");
                logger.LogError(ex, "Runtime initialization failed. Retrying without stopping the HTTP service.");
            }
        }
    }

    private async Task EnterStandbyAsync(string reason, CancellationToken ct)
    {
        activity.BeginDraining("Standby requested. Waiting for current operations to finish.");

        await activity.WaitForIdleAsync(ct);
        ct.ThrowIfCancellationRequested();

        activity.EnterStandby(reason);
        logger.LogInformation("Runtime is Standby. Previous operations have finished.");
    }
}