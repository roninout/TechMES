using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

internal sealed record PostgreSqlAutomaticFailbackOptions
{
    public const string SectionName = "PostgreSqlAutomaticFailback";

    public bool Enabled { get; init; }
    public string PreferredPrimary { get; init; } = "";
    public int RecoveryThreshold { get; init; } = 6;
    public int MaxReplayLagBytes { get; init; } = 1048576;
}

internal sealed class PostgreSqlAutomaticFailbackWorker(IOptions<PostgreSqlAutomaticFailbackOptions> options, IOptions<PostgreSqlNetworkOptions> network, IOptions<PostgreSqlControlOptions> control, PostgreSqlNodeMonitor monitor, PostgreSqlCommandStore store, ILogger<PostgreSqlAutomaticFailbackWorker> logger) : BackgroundService
{
    private readonly record struct PairSnapshot(int Timeline, DateTimeOffset SourceCheck, DateTimeOffset TargetCheck);

    private int _confirmations;
    private PairSnapshot? _last;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            network.Value.Validate(control.Value);

            if (options.Value.PreferredPrimary != network.Value.PeerAddress || options.Value.RecoveryThreshold is < 1 or > 60 || options.Value.MaxReplayLagBytes < 0)
                throw new InvalidOperationException("Automatic failback requires PreferredPrimary to match PeerAddress, RecoveryThreshold between 1 and 60, and a nonnegative MaxReplayLagBytes.");

            using var certificate = PostgreSqlNodeCertificates.LoadLocal(network.Value);
            using var remote = new PostgreSqlSwitchRemote(network.Value, certificate);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(monitor.GetHealth().HealthPeriodSeconds));

            logger.LogInformation("PostgreSQL automatic failback enabled for {PreferredPrimary}. Required healthy checks: {Threshold}, max replay lag: {MaxReplayLagBytes} bytes.", options.Value.PreferredPrimary, options.Value.RecoveryThreshold, options.Value.MaxReplayLagBytes);

            do
            {
                if (monitor.GetHealth() is { OwnsController: true, PostgreSqlRole: "Primary" })
                    await CheckAsync(remote, ProbeAsync, ct);
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
            logger.LogError(ex, "PostgreSQL automatic failback worker stopped.");
        }
    }

    private Task<bool> ProbeAsync(PostgreSqlSwitchPlan plan, int timeline, CancellationToken ct)
    {
        return PostgreSqlSwitchRunner.IsReadyForFailbackAsync(control.Value, plan, timeline, options.Value.MaxReplayLagBytes, store, ct);
    }

    internal async Task CheckAsync(IPostgreSqlSwitchRemote remote, Func<PostgreSqlSwitchPlan, int, CancellationToken, Task<bool>> probe, CancellationToken ct)
    {
        PostgreSqlSwitchPlan? started = null;

        try
        {
            if (File.Exists(store.CoordinatorPlanPath))
            {
                Reset();
                return;
            }

            var plan = new PostgreSqlSwitchPlan(Guid.NewGuid().ToString("N"), network.Value.NodeAddress, options.Value.PreferredPrimary, control.Value.ExpectedSystemIdentifier, DateTimeOffset.UtcNow);
            if (await ReadPairAsync(remote, plan, ct) is not { } pair)
            {
                Reset();
                return;
            }

            if (_last is { } last)
            {
                if (pair.Timeline != last.Timeline || pair.SourceCheck < last.SourceCheck || pair.TargetCheck < last.TargetCheck)
                    Reset();
                else if (pair.SourceCheck == last.SourceCheck || pair.TargetCheck == last.TargetCheck)
                    return;
            }

            if (!await probe(plan, pair.Timeline, ct))
            {
                Reset();
                return;
            }

            _last = pair;
            if (++_confirmations < options.Value.RecoveryThreshold)
                return;

            using var held = store.LockCoordinator();
            Reset();
            if (File.Exists(store.CoordinatorPlanPath) || await ReadPairAsync(remote, plan, ct) is not { } current)
                return;

            if (current.Timeline != pair.Timeline || current.SourceCheck < pair.SourceCheck || current.TargetCheck < pair.TargetCheck || !await probe(plan, current.Timeline, ct))
                return;

            ct.ThrowIfCancellationRequested();
            plan.Validate(network.Value, control.Value);
            PostgreSqlCommandStore.Write(store.CoordinatorPlanPath, plan);
            started = plan;

            Report("STARTED");
            await new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, Report, ct);
            File.Delete(store.CoordinatorPlanPath);

            void Report(string stage)
            {
                store.WriteCoordinatorProgress(plan, $"{stage} (automatic failback)");
                logger.LogInformation("PostgreSQL automatic failback {PlanId}: {Stage}", plan.Id, stage);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or HttpRequestException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or JsonException or Win32Exception))
        {
            Reset();

            if (started is not null)
                logger.LogWarning(ex, "PostgreSQL automatic failback {PlanId} did not finish. The active plan was retained.", started.Id);
            else
                logger.LogDebug(ex, "PostgreSQL automatic failback could not confirm the Primary and preferred Standby.");
        }
    }

    private static async Task<PairSnapshot?> ReadPairAsync(IPostgreSqlSwitchRemote remote, PostgreSqlSwitchPlan plan, CancellationToken ct)
    {
        var source = await remote.StatusAsync(plan.From, ct);
        if (source.ActivePlan is not null || source.Local.PostgreSqlRole != "Primary" || source.Local.Service?.ServiceState != "Running")
            return null;

        var target = await remote.StatusAsync(plan.To, ct);
        if (target.ActivePlan is not null || target.Local.PostgreSqlRole != "Standby" || target.Local.Service?.ServiceState != "Running")
            return null;

        PostgreSqlSwitchCoordinator.CheckInitial(plan, source, target);
        return new(source.Local.Timeline!.Value, source.Local.CheckedAtUtc!.Value, target.Local.CheckedAtUtc!.Value);
    }

    private void Reset()
    {
        _confirmations = 0;
        _last = null;
    }
}
