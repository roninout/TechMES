using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

internal sealed record PostgreSqlAutomaticFailoverOptions
{
    public const string SectionName = "PostgreSqlAutomaticFailover";

    public bool Enabled { get; init; }
    public int FailureThreshold { get; init; } = 3;
}

// Только локальная реплика запускает существующий аварийный план через mTLS.
internal sealed class PostgreSqlAutomaticFailoverWorker(IOptions<PostgreSqlAutomaticFailoverOptions> options, IOptions<PostgreSqlNetworkOptions> network, IOptions<PostgreSqlControlOptions> control, PostgreSqlNodeMonitor monitor, PostgreSqlCommandStore store, ILogger<PostgreSqlAutomaticFailoverWorker> logger) : BackgroundService
{
    private int _failures;
    private DateTimeOffset? _lastSourceCheck;
    private int? _timeline;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (options.Value.FailureThreshold is < 1 or > 10)
                throw new InvalidOperationException("PostgreSQL automatic failover FailureThreshold must be between 1 and 10.");

            network.Value.Validate(control.Value);
            using var certificate = PostgreSqlNodeCertificates.LoadLocal(network.Value);
            using var remote = new PostgreSqlSwitchRemote(network.Value, certificate);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(monitor.GetHealth().HealthPeriodSeconds));

            logger.LogInformation("PostgreSQL automatic failover enabled. Stopped Primary confirmations required: {Threshold}.", options.Value.FailureThreshold);

            do
            {
                if (monitor.GetHealth().OwnsController)
                    await CheckAsync(remote, ct);
                else
                    Reset();
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Ошибка автоматического координатора не останавливает Runtime и локальный контроллер.
            logger.LogError(ex, "PostgreSQL automatic failover worker stopped.");
        }
    }

    internal async Task CheckAsync(IPostgreSqlSwitchRemote remote, CancellationToken ct)
    {
        PostgreSqlSwitchPlan? started = null;

        try
        {
            if (File.Exists(store.CoordinatorPlanPath))
            {
                Reset();
                using var recoveryLock = store.LockCoordinator();
                if (!File.Exists(store.CoordinatorPlanPath))
                    return;

                var pending = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(store.CoordinatorPlanPath);
                pending.Validate(network.Value, control.Value);
                if (!pending.Emergency || pending.To != network.Value.NodeAddress)
                    return;

                // Подтверждение Promote сохраняется на новом Primary и переживает перезапуск контроллера.
                var promote = await remote.ReadAsync(pending.To, "Promote", pending, ct);
                if (promote.State != "Succeeded" || promote.Id != pending.OperationId("Promote"))
                    return;

                var rejoin = await remote.ReadAsync(pending.From, "Rejoin", pending, ct);
                if (rejoin.Id != pending.OperationId("Rejoin") || rejoin.State is not ("Missing" or "Succeeded"))
                    return;

                if (rejoin.State == "Missing")
                {
                    var rebuild = await remote.ReadAsync(pending.From, "Rebuild", pending, ct);
                    if (rebuild.Id != pending.OperationId("Rebuild") || rebuild.State != "Missing")
                        return;
                }

                var coordinator = new PostgreSqlSwitchCoordinator(remote);
                started = pending;
                if (rejoin.State == "Succeeded")
                    await coordinator.CompleteEmergencyRecoveryAsync(pending, stage => Report(pending, stage), ct);
                else
                    await coordinator.RecoverEmergencyAsync(pending, stage => Report(pending, stage), ct);
                File.Delete(store.CoordinatorPlanPath);
                return;
            }

            var plan = new PostgreSqlSwitchPlan(Guid.NewGuid().ToString("N"), network.Value.PeerAddress, network.Value.NodeAddress, control.Value.ExpectedSystemIdentifier, DateTimeOffset.UtcNow) { Emergency = true };
            var source = await ReadStoppedPrimaryAsync(remote, plan, ct);

            if (source is null)
            {
                Reset();
                return;
            }

            if (_timeline != source.Timeline || source.CheckedAtUtc < _lastSourceCheck)
                Reset();

            // Повторное чтение того же снимка не считается новой проверкой контроллера.
            if (_lastSourceCheck == source.CheckedAtUtc)
                return;

            _lastSourceCheck = source.CheckedAtUtc;
            _timeline = source.Timeline;

            if (++_failures < options.Value.FailureThreshold)
                return;

            using var held = store.LockCoordinator();
            if (File.Exists(store.CoordinatorPlanPath))
            {
                Reset();
                return;
            }

            // Проверяем пару ещё раз под той же блокировкой, которую держит CLI.
            var current = await ReadStoppedPrimaryAsync(remote, plan, ct);
            Reset();
            if (current is null || current.Timeline != source.Timeline || current.CheckedAtUtc < source.CheckedAtUtc)
                return;

            ct.ThrowIfCancellationRequested();
            plan.Validate(network.Value, control.Value);
            PostgreSqlCommandStore.Write(store.CoordinatorPlanPath, plan);
            started = plan;

            Report(plan, "Automatic emergency switch started.");
            await new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, stage => Report(plan, stage), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or HttpRequestException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or JsonException or TimeoutException))
        {
            Reset();

            if (started is not null)
                logger.LogWarning(ex, "PostgreSQL automatic failover {PlanId} did not finish. The active plan was retained.", started.Id);
            else
                logger.LogDebug(ex, "PostgreSQL automatic failover could not verify the pair or the retained emergency plan.");
        }
    }

    private void Report(PostgreSqlSwitchPlan plan, string stage)
    {
        store.WriteCoordinatorProgress(plan, stage);
        logger.LogInformation("PostgreSQL automatic failover {PlanId}: {Stage}", plan.Id, stage);
    }

    private static async Task<PostgreSqlLocalHealthResponse?> ReadStoppedPrimaryAsync(IPostgreSqlSwitchRemote remote, PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        var target = await remote.StatusAsync(plan.To, ct);
        if (target.ActivePlan is not null || target.Local.PostgreSqlRole != "Standby" || target.Local.Service?.ServiceState != "Running")
            return null;

        var source = await remote.StatusAsync(plan.From, ct);
        if (source.ActivePlan is not null || source.Local.PostgreSqlRole != "Stopped")
            return null;

        // Общая проверка свежести, идентификатора, timeline, прав и отсутствия процессов/порта.
        PostgreSqlSwitchCoordinator.CheckInitial(plan, source, target);
        return source.Local;
    }

    private void Reset()
    {
        _failures = 0;
        _lastSourceCheck = null;
        _timeline = null;
    }
}
