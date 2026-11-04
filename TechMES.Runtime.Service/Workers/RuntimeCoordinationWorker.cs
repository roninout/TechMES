using System.Diagnostics;
using Microsoft.Extensions.Options;
using TechMES.Application.Runtime;
using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.CtApi.Settings;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Автоматически получает и продлевает разрешение владельца в общей Main DB.
/// Primary имеет приоритет; Secondary уступает после нескольких успешных проверок Primary.
/// </summary>
public sealed class RuntimeCoordinationWorker(IRuntimeLeadershipStore store, RuntimeActivityState activity, IOptions<RuntimeOptions> options, IOptions<CtApiOptions> healthOptions, ILogger<RuntimeCoordinationWorker> logger) : BackgroundService
{
    private Guid? _leaseId;
    private bool _releasing;
    private int _successes;
    private int _failures;
    private long _cooldownUntil;
    private RuntimeLeadershipSnapshot? _lastSnapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Redundancy;

        if (!settings.Enabled)
            return;

        var periodSeconds = Math.Max(5, healthOptions.Value.HealthCheckPeriodSeconds);
        var leaseSeconds = checked(periodSeconds * (settings.FailureThreshold + 2));
        var timeoutSeconds = Math.Clamp(periodSeconds / 2, 1, 5);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodSeconds));

        try
        {
            do
            {
                await CheckAsync(settings, periodSeconds, leaseSeconds, timeoutSeconds, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            activity.RevokeOwnership("Runtime is stopping.");

            // При незавершённой работе не освобождаем общее разрешение досрочно.
            if (_leaseId is { } leaseId && activity.GetStatus().PendingOperations == 0)
            {
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    await store.ReleaseAsync(settings.Scope, activity.GetStatus().InstanceId, leaseId, stop.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Runtime ownership release failed during shutdown. The stored lease will expire.");
                }
            }
        }
    }

    private async Task CheckAsync(RuntimeRedundancyOptions settings, int periodSeconds, int leaseSeconds, int timeoutSeconds, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var state = activity.GetStatus();

        if (_leaseId is not null && state.Mode == "Faulted")
        {
            BeginRelease("Runtime initialization failed. Returning ownership.");
            _cooldownUntil = Stopwatch.GetTimestamp() + (long)leaseSeconds * Stopwatch.Frequency;
        }

        var coolingDown = Stopwatch.GetTimestamp() < _cooldownUntil;
        var canAcquire = !_releasing && !coolingDown && state.PendingOperations == 0 && _successes + 1 >= settings.RecoveryThreshold;
        var advertise = !coolingDown && !_releasing && state.Mode is not ("Faulted" or "Draining");
        var request = new RuntimeLeadershipRequest(settings.Scope, state.NodeId, state.InstanceId, settings.Role, canAcquire, advertise, leaseSeconds, periodSeconds * (settings.FailureThreshold + 1), settings.RecoveryThreshold);

        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var snapshot = await store.TouchAsync(request, stop.Token);
            _lastSnapshot = snapshot;
            _successes = Math.Min(_successes + 1, settings.RecoveryThreshold);
            _failures = 0;

            SetStatus(settings, snapshot.IsOwner ? "Ownership confirmed." : $"Waiting. Owner: {snapshot.OwnerNodeId ?? "None"}.");

            if (!snapshot.IsOwner)
            {
                _leaseId = null;
                _releasing = false;

                if (!activity.GetStatus().StandbyRequested)
                    activity.RevokeOwnership("Another Runtime owns the lease or stable connection checks are still running.");

                return;
            }

            _leaseId = snapshot.LeaseId ?? throw new InvalidOperationException("Runtime ownership response has no lease identifier.");

            if (snapshot.YieldToPrimary)
                BeginRelease("Preferred Primary is stable. Preparing to transfer Runtime activity.");

            if (_releasing)
            {
                // Пока старые операции завершаются, TouchAsync продолжает продлевать lease.
                if (activity.GetStatus().PendingOperations != 0)
                    return;

                await store.ReleaseAsync(settings.Scope, state.InstanceId, _leaseId.Value, stop.Token);
                _leaseId = null;
                _releasing = false;
                _successes = 0;
                _lastSnapshot = null;
                activity.EnterStandby("Runtime activity was released.");
                SetStatus(settings, "Ownership released after all operations finished.");
                return;
            }

            if (!activity.HasOwnership(_leaseId.Value))
            {
                activity.RevokeOwnership("Preparing a new Runtime ownership lease.");

                if (activity.GetStatus().PendingOperations != 0)
                {
                    _releasing = true;
                    return;
                }

                activity.EnterStandby("Previous operations have finished.");
            }

            // Отсчитываем срок от НАЧАЛА запроса, с запасом до окончания срока в БД.
            var remaining = TimeSpan.FromSeconds(leaseSeconds - timeoutSeconds) - Stopwatch.GetElapsedTime(started);
            activity.RenewOwnership(_leaseId.Value, remaining);
            activity.SetStandbyRequested(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _successes = 0;
            _failures++;

            if (_failures >= settings.FailureThreshold || _leaseId is { } leaseId && !activity.HasOwnership(leaseId))
                BeginRelease("Runtime coordination is unavailable. Work is blocked.");

            SetStatus(settings, "Coordination request failed. See Runtime logs.");
            logger.LogWarning(ex, "Runtime coordination failed. ConsecutiveFailures={Failures}.", _failures);
        }
    }

    private void BeginRelease(string reason)
    {
        if (_releasing)
            return;

        _releasing = true;
        activity.RevokeOwnership(reason);
    }

    private void SetStatus(RuntimeRedundancyOptions settings, string message)
    {
        activity.SetCoordinationStatus(new RuntimeCoordinationStatusResponse { Enabled = true, Role = settings.Role, OwnerNodeId = _lastSnapshot?.OwnerNodeId, OwnerInstanceId = _lastSnapshot?.OwnerInstanceId, LeaseId = _lastSnapshot?.LeaseId, ExpiresAtUtc = _lastSnapshot?.ExpiresAtUtc, ConsecutiveFailures = _failures, Message = message });
    }
}