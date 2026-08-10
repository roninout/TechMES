using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service;

/// <summary>
/// Отправляет heartbeat независимо от calculation scheduler
/// и поддерживает локальное состояние execution lease.
/// </summary>
internal sealed class CalcHeartbeatWorker(ILogger<CalcHeartbeatWorker> logger, IRuntimeCalcClient runtimeClient,CalcServiceIdentity identity, CalcServiceLeaseState leaseState,IOptions<CalcRuntimeClientOptions> options) : BackgroundService
{
    private bool? _lastSendSucceeded;
    private bool? _lastLeaseOwned;
    private string _lastLeaseEpoch = "";
    private long _lastLeaseToken;

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
    /// Обновляет heartbeat и локальное ownership-state.
    /// Новый Runtime epoch считается новым lease даже при совпадении token.
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
            var response = await runtimeClient.SendHeartbeatAsync(request, ct);
            leaseState.Apply(response);

            if (_lastSendSucceeded != true)
                logger.LogInformation("Calc Service heartbeat connected to Runtime.Service. InstanceId={InstanceId}.", identity.InstanceId);

            if (response.IsLeaseOwner)
            {
                var leaseChanged = _lastLeaseOwned != true
                    || _lastLeaseToken != response.LeaseToken
                    || !string.Equals(_lastLeaseEpoch, response.LeaseEpoch, StringComparison.Ordinal);

                if (leaseChanged)
                {
                    logger.LogInformation(
                        "Calc Service execution lease acquired. InstanceId={InstanceId}, LeaseEpoch={LeaseEpoch}, LeaseToken={LeaseToken}, ExpiresAtUtc={ExpiresAtUtc}.",
                        identity.InstanceId, response.LeaseEpoch, response.LeaseToken, response.LeaseExpiresAtUtc);
                }
            }
            else if (_lastLeaseOwned != false)
            {
                logger.LogWarning(
                    "Calc Service is running as standby. InstanceId={InstanceId}, LeaseOwner={LeaseOwner}, LeaseEpoch={LeaseEpoch}, LeaseToken={LeaseToken}.",
                    identity.InstanceId, response.LeaseOwnerInstanceId, response.LeaseEpoch, response.LeaseToken);
            }

            _lastSendSucceeded = true;
            _lastLeaseOwned = response.IsLeaseOwner;
            _lastLeaseEpoch = response.LeaseEpoch;
            _lastLeaseToken = response.LeaseToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            /*
             * LeaseState не очищаем сразу.
             * Последний подтверждённый lease естественно закончится
             * по собственному локальному timeout.
             */
            if (_lastSendSucceeded != false)
                logger.LogWarning(ex, "Cannot send Calc Service heartbeat to Runtime.Service.");

            _lastSendSucceeded = false;
        }
    }
}