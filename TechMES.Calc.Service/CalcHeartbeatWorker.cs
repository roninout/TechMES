using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Calc.Service.Runtime;
using TechMES.Calc.Service.Settings;
using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service;

/// <summary>
/// Отправляет heartbeat независимо от calculation scheduler,
/// поддерживает локальный execution lease и инициирует Calc Catalog refresh
/// при получении нового lease.
/// </summary>
internal sealed class CalcHeartbeatWorker(ILogger<CalcHeartbeatWorker> logger, IRuntimeCalcClient runtimeClient, CalcServiceIdentity identity, CalcServiceLeaseState leaseState, IOptions<CalcRuntimeClientOptions> options) : BackgroundService
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
    ///
    /// При новом lease текущий owner один раз инициирует загрузку
    /// SCADA Calc Catalog в Runtime.
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
            {
                logger.LogInformation(
                    "Calc Service heartbeat connected to Runtime.Service. InstanceId={InstanceId}.",
                    identity.InstanceId);
            }

            if (response.IsLeaseOwner)
            {
                var leaseChanged =
                    _lastLeaseOwned != true
                    || _lastLeaseToken != response.LeaseToken
                    || !string.Equals(
                        _lastLeaseEpoch,
                        response.LeaseEpoch,
                        StringComparison.Ordinal);

                if (leaseChanged)
                {
                    logger.LogInformation(
                        "Calc Service execution lease acquired. InstanceId={InstanceId}, LeaseEpoch={LeaseEpoch}, LeaseToken={LeaseToken}, ExpiresAtUtc={ExpiresAtUtc}.",
                        identity.InstanceId,
                        response.LeaseEpoch,
                        response.LeaseToken,
                        response.LeaseExpiresAtUtc);

                    await TryRefreshModelCatalogAsync(ct);
                }
            }
            else if (_lastLeaseOwned != false)
            {
                logger.LogWarning(
                    "Calc Service is running as standby. InstanceId={InstanceId}, LeaseOwner={LeaseOwner}, LeaseEpoch={LeaseEpoch}, LeaseToken={LeaseToken}.",
                    identity.InstanceId,
                    response.LeaseOwnerInstanceId,
                    response.LeaseEpoch,
                    response.LeaseToken);
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

    /// <summary>
    /// Ошибка Calc Catalog не должна ломать heartbeat и lease.
    ///
    /// Расчёты существующих Jobs могут продолжаться,
    /// даже если discovery новых SCADA models временно не работает.
    /// </summary>
    private async Task TryRefreshModelCatalogAsync(CancellationToken ct)
    {
        try
        {
            var catalog = await runtimeClient.RefreshModelCatalogAsync(ct);

            if (!catalog.IsAvailable)
            {
                logger.LogWarning(
                    "Calc SCADA catalog provider is unavailable. Error={Error}.",
                    catalog.ErrorMessage);

                return;
            }

            logger.LogInformation(
                "Calc SCADA catalog loaded after lease acquisition. Total={Total}, Stations={StationCount}, Types={TypeCount}, LoadedAtUtc={LoadedAtUtc}.",
                catalog.TotalCount,
                catalog.Stations.Count,
                catalog.Types.Count,
                catalog.LoadedAtUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Cannot refresh Calc SCADA catalog. Calculation scheduler will continue with existing Jobs.");
        }
    }
}