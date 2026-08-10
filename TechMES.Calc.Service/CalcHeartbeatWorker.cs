using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service;

/// <summary>
/// Независимо от scheduler-а подтверждает Runtime, что Calc.Service работает.
///
/// Heartbeat вынесен из CalcWorker, чтобы долгий расчёт или configuration refresh
/// не влиял на контроль доступности службы.
/// </summary>
internal sealed class CalcHeartbeatWorker(ILogger<CalcHeartbeatWorker> logger, IRuntimeCalcClient runtimeClient, CalcServiceIdentity identity, IOptions<CalcRuntimeClientOptions> options) : BackgroundService
{
    private bool? _lastSendSucceeded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);

        try
        {
            await SendHeartbeatAsync(stoppingToken);

            using var timer = new PeriodicTimer(period);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SendHeartbeatAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка Windows-службы.
        }
    }

    /// <summary>
    /// Отправляет heartbeat и логирует только изменение доступности Runtime,
    /// чтобы не засорять журнал сообщением каждые пять секунд.
    /// </summary>
    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var request = new CalcServiceHeartbeatRequest
        {
            InstanceId = identity.InstanceId,
            MachineName = identity.MachineName,
            ProcessId = identity.ProcessId,
            ServiceVersion = identity.ServiceVersion,
            StartedAtUtc = identity.StartedAtUtc
        };

        try
        {
            await runtimeClient.SendHeartbeatAsync(request, ct);

            if (_lastSendSucceeded != true)
                logger.LogInformation("Calc Service heartbeat connected to Runtime.Service. InstanceId={InstanceId}.", identity.InstanceId);

            _lastSendSucceeded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_lastSendSucceeded != false)
                logger.LogWarning(ex, "Cannot send Calc Service heartbeat to Runtime.Service.");

            _lastSendSucceeded = false;
        }
    }
}