using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;
using TechMES.PostgreSql.Control;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlNetworkTests
{
    private static readonly PostgreSqlSwitchPlan Plan = new("123456781234123412341234567890ab", "10.77.12.61", "10.77.12.62", "7703468038317098096", DateTimeOffset.UtcNow);

    // Id сохраняется при повторе и различается у фаз.
    [Fact]
    public void OperationIdsAreStableAndDifferent()
    {
        Assert.Equal(Plan.OperationId("Fence"), Plan.OperationId("Fence"));
        Assert.NotEqual(Plan.OperationId("Fence"), Plan.OperationId("Promote"));
        Assert.Matches("^[0-9a-f]{32}$", Plan.OperationId("Rejoin"));
    }

    // Последующая фаза не подаётся до подтверждения предыдущей.
    [Fact]
    public async Task FullCycleRunsInOrderAndReleasesBothNodes()
    {
        var remote = new Remote();

        await new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None);

        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61", "Promote:10.77.12.62", "Rejoin:10.77.12.61", "complete:10.77.12.61", "complete:10.77.12.62" }, remote.Calls);
    }

    [Fact]
    public async Task EmergencyOnlyFencesAndPromotesAndKeepsThePairReserved()
    {
        var plan = Plan with { Emergency = true };
        var remote = new Remote();

        await new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, _ => { }, CancellationToken.None);

        Assert.NotEqual(Plan.OperationId("Fence"), plan.OperationId("Fence"));
        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61", "Promote:10.77.12.62" }, remote.Calls);
    }

    [Fact]
    public async Task AutomaticFailoverCountsNewSnapshotsAndLeavesThePlanForConsoleRejoin()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateAutomaticWorker(3);
        var source = StoppedPrimary(DateTimeOffset.UtcNow.AddSeconds(-10));
        var remote = new Remote { StatusOverride = address => address == Plan.From ? source : RunningStandby() };

        await worker.CheckAsync(remote, CancellationToken.None);
        for (var duplicate = 0; duplicate < 3; duplicate++)
            await worker.CheckAsync(remote, CancellationToken.None);

        source = source with { Local = source.Local with { CheckedAtUtc = source.Local.CheckedAtUtc!.Value.AddSeconds(1) } };
        await worker.CheckAsync(remote, CancellationToken.None);
        Assert.Empty(remote.Calls);

        source = source with { Local = source.Local with { CheckedAtUtc = source.Local.CheckedAtUtc!.Value.AddSeconds(1) } };
        await worker.CheckAsync(remote, CancellationToken.None);
        await worker.CheckAsync(remote, CancellationToken.None);

        var pending = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(fixture.Store.CoordinatorPlanPath);
        Assert.True(pending.Emergency);
        Assert.Equal((Plan.From, Plan.To, Plan.SystemIdentifier), (pending.From, pending.To, pending.SystemIdentifier));
        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61", "Promote:10.77.12.62" }, remote.Calls);
        var journal = PostgreSqlCommandStore.Read<JsonElement>(Path.Combine(fixture.Store.NetworkRoot, "coordinator-" + pending.Id + ".json"));
        Assert.StartsWith("PROMOTED:", journal.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("running")]
    [InlineData("stale")]
    [InlineData("reserved")]
    [InlineData("different timeline")]
    [InlineData("listener present")]
    public async Task AutomaticFailoverResetsTheCountWhenThePairCannotBeConfirmed(string interruption)
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateAutomaticWorker(3);
        var firstCheck = DateTimeOffset.UtcNow.AddSeconds(-10);
        var source = StoppedPrimary(firstCheck);
        var unreachable = false;
        var remote = new Remote
        {
            StatusOverride = address => address != Plan.From ? RunningStandby() : unreachable ? throw new HttpRequestException("Peer unavailable") : source
        };
        await worker.CheckAsync(remote, CancellationToken.None);
        source = StoppedPrimary(firstCheck.AddSeconds(1));
        await worker.CheckAsync(remote, CancellationToken.None);

        source = StoppedPrimary(firstCheck.AddSeconds(2));
        unreachable = interruption == "unreachable";
        source = interruption switch
        {
            "running" => Status(Plan.From, "Primary"),
            "stale" => StoppedPrimary(firstCheck.AddMinutes(-5)),
            "reserved" => source with { ActivePlan = Plan },
            "different timeline" => source with { Local = source.Local with { Timeline = 3 } },
            "listener present" => source with { Local = source.Local with { Service = source.Local.Service! with { ListenerProcessId = 123 } } },
            _ => source
        };
        await worker.CheckAsync(remote, CancellationToken.None);
        Assert.Empty(remote.Calls);

        unreachable = false;
        source = StoppedPrimary(firstCheck.AddSeconds(3));
        await worker.CheckAsync(remote, CancellationToken.None);
        Assert.Empty(remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task AutomaticFailoverChecksTheSourceAgainBeforeReserving()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateAutomaticWorker(1);
        var sourceReads = 0;
        var remote = new Remote
        {
            StatusOverride = address => address != Plan.From ? RunningStandby() : ++sourceReads == 1 ? StoppedPrimary(DateTimeOffset.UtcNow) : Status(Plan.From, "Primary")
        };

        await worker.CheckAsync(remote, CancellationToken.None);

        Assert.Equal(2, sourceReads);
        Assert.Empty(remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task AutomaticFailoverDoesNotStartWhileConsoleHoldsTheLock()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateAutomaticWorker(1);
        using var held = fixture.Store.LockCoordinator();
        var remote = new Remote { StatusOverride = address => address == Plan.From ? StoppedPrimary(DateTimeOffset.UtcNow) : RunningStandby() };

        await worker.CheckAsync(remote, CancellationToken.None);

        Assert.Empty(remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task AutomaticFailoverRetainsThePlanWithoutPromotingOrRetryingAfterFenceFailure()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateAutomaticWorker(1);
        var remote = new Remote
        {
            Failed = "Fence",
            StatusOverride = address => address == Plan.From ? StoppedPrimary(DateTimeOffset.UtcNow) : RunningStandby()
        };

        await worker.CheckAsync(remote, CancellationToken.None);
        await worker.CheckAsync(remote, CancellationToken.None);

        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61" }, remote.Calls);
        Assert.True(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task AutomaticFailbackRequiresDistinctSamplesOnBothNodesAndFinishesThePlannedSwitch()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateFailbackWorker(2);
        var sourceCheck = DateTimeOffset.UtcNow.AddSeconds(-10);
        var targetCheck = sourceCheck;
        var remote = new Remote { StatusOverride = address => RunningNode(address, address == Plan.From ? sourceCheck : targetCheck) };
        var probes = 0;

        Task<bool> Probe(PostgreSqlSwitchPlan plan, int timeline, CancellationToken ct)
        {
            Assert.False(plan.Emergency);
            Assert.Equal((Plan.From, Plan.To, 2), (plan.From, plan.To, timeline));
            probes++;
            return Task.FromResult(true);
        }

        await worker.CheckAsync(remote, Probe, CancellationToken.None);
        await worker.CheckAsync(remote, Probe, CancellationToken.None);
        sourceCheck = sourceCheck.AddSeconds(1);
        await worker.CheckAsync(remote, Probe, CancellationToken.None);
        Assert.Equal(1, probes);
        Assert.Empty(remote.Calls);

        targetCheck = targetCheck.AddSeconds(1);
        await worker.CheckAsync(remote, Probe, CancellationToken.None);

        Assert.Equal(3, probes);
        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61", "Promote:10.77.12.62", "Rejoin:10.77.12.61", "complete:10.77.12.61", "complete:10.77.12.62" }, remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
        var journal = PostgreSqlCommandStore.Read<JsonElement>(Assert.Single(Directory.GetFiles(fixture.Store.NetworkRoot, "coordinator-*.json")));
        Assert.Equal("COMPLETED: Primary=10.77.12.62; Standby=10.77.12.61. (automatic failback)", journal.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("reserved")]
    [InlineData("too far behind")]
    [InlineData("new timeline")]
    public async Task AutomaticFailbackWaitsForNewConfirmationsAfterAnInterruption(string interruption)
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateFailbackWorker(3);
        var time = DateTimeOffset.UtcNow.AddSeconds(-10);
        var timeline = 2;
        var problem = false;
        var remote = new Remote
        {
            StatusOverride = address => address == Plan.To && problem && interruption == "unreachable"
                ? throw new HttpRequestException("Peer unavailable")
                : RunningNode(address, time, timeline) with { ActivePlan = address == Plan.To && problem && interruption == "reserved" ? Plan : null }
        };
        Task<bool> Probe(PostgreSqlSwitchPlan plan, int timeline, CancellationToken ct) => Task.FromResult(!problem || interruption != "too far behind");

        await worker.CheckAsync(remote, Probe, CancellationToken.None);
        time = time.AddSeconds(1);
        await worker.CheckAsync(remote, Probe, CancellationToken.None);

        problem = true;
        if (interruption == "new timeline")
            timeline++;
        time = time.AddSeconds(1);
        await worker.CheckAsync(remote, Probe, CancellationToken.None);

        problem = false;
        time = time.AddSeconds(1);
        await worker.CheckAsync(remote, Probe, CancellationToken.None);

        Assert.Empty(remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task AutomaticFailbackRechecksReplicationBeforeReserving()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateFailbackWorker(1);
        var checkedAt = DateTimeOffset.UtcNow;
        var remote = new Remote { StatusOverride = address => RunningNode(address, checkedAt) };
        var probes = 0;

        await worker.CheckAsync(remote, (_, _, _) => Task.FromResult(++probes == 1), CancellationToken.None);

        Assert.Equal(2, probes);
        Assert.Empty(remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task AutomaticFailbackRetainsThePlanAfterFenceFailureAndDoesNotStartAnother()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateFailbackWorker(1);
        var remote = new Remote { StatusOverride = address => RunningNode(address, DateTimeOffset.UtcNow), Failed = "Fence" };

        await worker.CheckAsync(remote, (_, _, _) => Task.FromResult(true), CancellationToken.None);
        var pending = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(fixture.Store.CoordinatorPlanPath);
        await worker.CheckAsync(remote, (_, _, _) => Task.FromResult(true), CancellationToken.None);

        Assert.False(pending.Emergency);
        Assert.Equal(pending, PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(fixture.Store.CoordinatorPlanPath));
        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61" }, remote.Calls);
    }

    private static PostgreSqlRemoteStatus RunningNode(string address, DateTimeOffset checkedAt, int timeline = 2)
    {
        var node = Status(address, address == Plan.From ? "Primary" : "Standby");
        return node with { Local = node.Local with { CheckedAtUtc = checkedAt, Timeline = timeline, Service = new() { ServiceState = "Running", ManagedByService = true } } };
    }

    private static PostgreSqlRemoteStatus StoppedPrimary(DateTimeOffset checkedAt)
    {
        var source = Status(Plan.From, "Stopped");
        return source with { Local = source.Local with { ClusterState = "shut down", CheckedAtUtc = checkedAt, Service = new() { ServiceState = "Stopped" } } };
    }

    private static PostgreSqlRemoteStatus RunningStandby()
    {
        var target = Status(Plan.To, "Standby");
        return target with { Local = target.Local with { Service = new() { ServiceState = "Running", ManagedByService = true } } };
    }

    [Fact]
    public async Task EmergencyProofFromAnotherOperationIsRejected()
    {
        var remote = new Remote { WrongEmergencyId = true };
        var plan = Plan with { Emergency = true };

        await Assert.ThrowsAsync<IOException>(() => new PostgreSqlSwitchCoordinator(remote).RunAsync(plan, _ => { }, CancellationToken.None));

        Assert.DoesNotContain(remote.Calls, call => call.StartsWith("Promote:"));
    }

    [Fact]
    public async Task EmergencyTargetRequiresMatchingProofFromSourceController()
    {
        var plan = Plan with { Emergency = true };
        var remote = new Remote();
        var recorded = (await remote.SubmitAsync(plan.From, "Fence", new(plan, null), CancellationToken.None)).Fence!.Value;

        await PostgreSqlSwitchCoordinator.VerifyEmergencyFenceAsync(remote, plan, recorded);

        await Assert.ThrowsAsync<IOException>(() => PostgreSqlSwitchCoordinator.VerifyEmergencyFenceAsync(remote, plan, JsonSerializer.SerializeToElement(new { State = "Fenced" })));

        remote.FenceReadFailures = 1;
        await Assert.ThrowsAsync<IOException>(() => PostgreSqlSwitchCoordinator.VerifyEmergencyFenceAsync(remote, plan, recorded));
    }

    [Theory]
    [InlineData("in production", 0, 0, true)]
    [InlineData("shut down", 0, 0, true)]
    [InlineData("shut down in recovery", 0, 0, false)]
    [InlineData("in production", 123, 0, false)]
    [InlineData("in production", 0, 123, false)]
    public void EmergencyRequiresAnActuallyStoppedPrimary(string clusterState, int postmaster, int listener, bool expected)
    {
        var plan = Plan with { Emergency = true };
        var source = Status(plan.From, "Stopped");
        source = source with
        {
            Local = source.Local with
            {
                ClusterState = clusterState,
                Service = new() { ServiceState = "Stopped", PostmasterProcessId = postmaster, ListenerProcessId = listener }
            }
        };
        var target = Status(plan.To, "Standby");

        if (expected)
            PostgreSqlSwitchCoordinator.CheckInitial(plan, source, target);
        else
            Assert.Throws<InvalidOperationException>(() => PostgreSqlSwitchCoordinator.CheckInitial(plan, source, target));
    }

    [Fact]
    public async Task EmergencyRejoinKeepsTheFenceIdAndBlocksEarlyCompletion()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var plan = Plan with { Emergency = true };
        var id = plan.OperationId("Fence");
        var request = new PostgreSqlCommand(id, "Fence", plan.From, plan.To, plan.SystemIdentifier, DateTimeOffset.UtcNow, null) { EmergencyFenceId = id };

        Assert.Throws<InvalidOperationException>(() => PostgreSqlSwitchInbox.AssertCommandAllowed(fixture.Store.Root, request));

        await fixture.Inbox.ReserveAsync(plan, CancellationToken.None);
        var queued = await fixture.Inbox.SubmitAsync("Fence", new(plan, null), CancellationToken.None);
        var stored = PostgreSqlCommandStore.Read<PostgreSqlCommand>(Path.Combine(fixture.Store.Root, id, "request.json"));

        Assert.Equal(id, queued.Id);
        Assert.Equal(id, stored.EmergencyFenceId);
        Assert.Equal(plan, PostgreSqlSwitchInbox.AssertCommandAllowed(fixture.Store.Root, stored));
        var rejoin = await fixture.Inbox.SubmitAsync("Rejoin", new(plan, JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
        var rejoinRequest = PostgreSqlCommandStore.Read<PostgreSqlCommand>(Path.Combine(fixture.Store.Root, rejoin.Id, "request.json"));
        Assert.Equal(id, rejoinRequest.EmergencyFenceId);
        Assert.Equal(plan, PostgreSqlSwitchInbox.AssertCommandAllowed(fixture.Store.Root, rejoinRequest));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.CompleteAsync(plan, CancellationToken.None));
    }

    [Fact]
    public async Task EmergencyRecoveryReadsExistingProofAndLeavesCurrentPrimaryInPlace()
    {
        var remote = new Remote();
        var plan = Plan with { Emergency = true };

        await new PostgreSqlSwitchCoordinator(remote).RecoverEmergencyAsync(plan, _ => { }, CancellationToken.None);

        Assert.Equal(new[] { "read-Fence:10.77.12.61", "read-Promote:10.77.12.62", "Rejoin:10.77.12.61", "complete:10.77.12.61", "complete:10.77.12.62" }, remote.Calls);
    }

    [Fact]
    public async Task EmergencyRebuildRunsOnTheFencedNodeAndKeepsTheCurrentPrimary()
    {
        var remote = new Remote();
        var plan = Plan with { Emergency = true };

        await new PostgreSqlSwitchCoordinator(remote).RecoverEmergencyAsync(plan, _ => { }, CancellationToken.None, rebuild: true);

        Assert.Equal(new[] { "read-Fence:10.77.12.61", "read-Promote:10.77.12.62", "Rebuild:10.77.12.61", "complete:10.77.12.61", "complete:10.77.12.62" }, remote.Calls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("RebuildRequired", true)]
    [InlineData("Rejected", true)]
    [InlineData("Queued", false)]
    [InlineData("Running", false)]
    [InlineData("NeedsReview", false)]
    [InlineData("Succeeded", false)]
    public async Task RebuildOnlyFollowsAnUnattemptedOrSafelyStoppedRejoin(string? rejoinState, bool allowed)
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        var plan = Plan with { Emergency = true };
        var proof = (await new Remote().ReadAsync(plan.From, "Fence", plan, CancellationToken.None)).Fence!.Value;
        await fixture.Inbox.ReserveAsync(plan, CancellationToken.None);
        await fixture.Inbox.SubmitAsync("Fence", new(plan, null), CancellationToken.None);
        var fencePath = Path.Combine(fixture.Store.Root, plan.OperationId("Fence"));
        PostgreSqlCommandStore.Write(Path.Combine(fencePath, "fence.json"), proof);
        PostgreSqlCommandStore.Write(Path.Combine(fencePath, "script-result.json"), JsonSerializer.SerializeToElement(new { }));
        PostgreSqlCommandStore.Write(Path.Combine(fencePath, "journal.json"), new PostgreSqlCommandJournal(plan.OperationId("Fence"), "fingerprint", "Succeeded", DateTimeOffset.UtcNow, ""));

        if (rejoinState is not null)
        {
            await fixture.Inbox.SubmitAsync("Rejoin", new(plan, proof), CancellationToken.None);
            if (rejoinState != "Queued")
                PostgreSqlCommandStore.Write(Path.Combine(fixture.Store.Root, plan.OperationId("Rejoin"), "journal.json"), new PostgreSqlCommandJournal(plan.OperationId("Rejoin"), "fingerprint", rejoinState, DateTimeOffset.UtcNow, ""));
        }

        if (!allowed)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.SubmitAsync("Rebuild", new(plan, proof), CancellationToken.None));
            return;
        }

        var differentProof = JsonSerializer.SerializeToElement(new { State = "Fenced" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.SubmitAsync("Rebuild", new(plan, differentProof), CancellationToken.None));
        var result = await fixture.Inbox.SubmitAsync("Rebuild", new(plan, proof), CancellationToken.None);
        Assert.Equal("Queued", result.State);
        Assert.Equal(plan.OperationId("Rebuild"), result.Id);
        var command = PostgreSqlCommandStore.Read<PostgreSqlCommand>(Path.Combine(fixture.Store.Root, result.Id, "request.json"));
        Assert.Equal(plan, PostgreSqlSwitchInbox.AssertCommandAllowed(fixture.Store.Root, command));
        if (rejoinState is null)
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.SubmitAsync("Rejoin", new(plan, proof), CancellationToken.None));
        else
            Assert.Equal(rejoinState, (await fixture.Inbox.SubmitAsync("Rejoin", new(plan, proof), CancellationToken.None)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.CompleteAsync(plan, CancellationToken.None));

        var rebuildPath = Path.Combine(fixture.Store.Root, result.Id);
        PostgreSqlCommandStore.Write(Path.Combine(rebuildPath, "script-result.json"), JsonSerializer.SerializeToElement(new { }));
        PostgreSqlCommandStore.Write(Path.Combine(rebuildPath, "journal.json"), new PostgreSqlCommandJournal(result.Id, "fingerprint", "Succeeded", DateTimeOffset.UtcNow, ""));
        await fixture.Inbox.CompleteAsync(plan, CancellationToken.None);
        Assert.Null(fixture.Inbox.Status().ActivePlan);
    }

    [Fact]
    public async Task NewPrimaryRequiresTheRemoteRebuildToFinishBeforeCompleting()
    {
        var plan = Plan with { Emergency = true };
        var remote = new Remote { MissingPhase = "Rejoin", Failed = "Rebuild" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlSwitchCoordinator.VerifyEmergencyRecoveryAsync(remote, plan, CancellationToken.None));
        remote.Failed = null;
        await PostgreSqlSwitchCoordinator.VerifyEmergencyRecoveryAsync(remote, plan, CancellationToken.None);

        Assert.Equal(new[] { "read-Rejoin:10.77.12.61", "read-Rebuild:10.77.12.61", "read-Rejoin:10.77.12.61", "read-Rebuild:10.77.12.61" }, remote.Calls);
    }

    [Theory]
    [InlineData("Fence")]
    [InlineData("Promote")]
    [InlineData("Rejoin")]
    public async Task EmergencyRecoveryRetainsBothReservationsWhenARequiredPhaseIsNotConfirmed(string failed)
    {
        var remote = new Remote { Failed = failed };
        var plan = Plan with { Emergency = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgreSqlSwitchCoordinator(remote).RecoverEmergencyAsync(plan, _ => { }, CancellationToken.None));

        Assert.DoesNotContain(remote.Calls, call => call.StartsWith("complete:"));

        if (failed != "Rejoin")
            Assert.DoesNotContain(remote.Calls, call => call.StartsWith("Rejoin:"));
    }

    // Неопределённый результат прекращает переход к следующему этапу.
    [Theory]
    [InlineData("Fence")]
    [InlineData("Promote")]
    [InlineData("Rejoin")]
    public async Task UnconfirmedPhaseRetainsReservations(string failed)
    {
        var remote = new Remote { Failed = failed };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None));

        Assert.DoesNotContain(remote.Calls, item => item.StartsWith("complete:"));

        if (failed == "Fence")
            Assert.DoesNotContain(remote.Calls, item => item.StartsWith("Promote:"));

        if (failed == "Promote")
            Assert.DoesNotContain(remote.Calls, item => item.StartsWith("Rejoin:"));
    }

    // Отказ резервирования запрещает даже Fence.
    [Fact]
    public async Task ReservationFailureDoesNotStartDatabaseChanges()
    {
        var remote = new Remote { FailReservation = true };

        await Assert.ThrowsAsync<IOException>(() => new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None));

        Assert.DoesNotContain(remote.Calls, item => item.StartsWith("Fence:"));
    }

    // Чужой proof не передаётся на Promote.
    [Fact]
    public async Task ForeignFenceProofIsRejected()
    {
        var remote = new Remote { WrongProof = true };

        await Assert.ThrowsAsync<IOException>(() => new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None));

        Assert.DoesNotContain(remote.Calls, item => item.StartsWith("Promote:"));
    }

    // Если после потери ответа операция не найдена, повышение блокируется.
    [Fact]
    public async Task LostFenceResponseDoesNotPromoteStandby()
    {
        var remote = new Remote { LoseFenceResponse = true, MissingPhase = "Fence" };

        await Assert.ThrowsAsync<HttpRequestException>(() => new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None));

        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61", "read-Fence:10.77.12.61" }, remote.Calls);
    }

    [Fact]
    public async Task AutomaticFailbackContinuesAfterReadingConfirmedFenceWithoutSubmittingItAgain()
    {
        using var fixture = new Fixture();
        using var worker = fixture.CreateFailbackWorker(1);
        var remote = new Remote
        {
            LoseFenceResponse = true,
            FenceReadFailures = 1,
            StatusOverride = address => RunningNode(address, DateTimeOffset.UtcNow)
        };

        await worker.CheckAsync(remote, (_, _, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61", "read-Fence:10.77.12.61", "read-Fence:10.77.12.61", "Promote:10.77.12.62", "Rejoin:10.77.12.61", "complete:10.77.12.61", "complete:10.77.12.62" }, remote.Calls);
        Assert.False(File.Exists(fixture.Store.CoordinatorPlanPath));
    }

    [Fact]
    public async Task FenceReadFailureWhilePollingDoesNotDiscardThePendingOperation()
    {
        var remote = new Remote { QueueFence = true, FenceReadFailures = 1 };

        await new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None);

        Assert.Single(remote.Calls, call => call == "Fence:10.77.12.61");
        Assert.Equal(2, remote.Calls.Count(call => call == "read-Fence:10.77.12.61"));
        Assert.Contains("Promote:10.77.12.62", remote.Calls);
    }

    // Ответ Succeeded не заменяет подтверждение изоляции.
    [Fact]
    public async Task FenceWithoutProofDoesNotPromoteStandby()
    {
        var remote = new Remote { MissingFenceProof = true };

        await Assert.ThrowsAsync<IOException>(() => new PostgreSqlSwitchCoordinator(remote).RunAsync(Plan, _ => { }, CancellationToken.None));

        Assert.Equal(new[] { "reserve:10.77.12.61", "reserve:10.77.12.62", "Fence:10.77.12.61" }, remote.Calls);
    }

    // Повтор Submit возвращает тот же request.json.
    [Fact]
    public async Task InboxSubmissionIsIdempotent()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);

        var first = await fixture.Inbox.SubmitAsync("Fence", new(Plan, null), CancellationToken.None);
        var path = Path.Combine(fixture.Store.Root, first.Id, "request.json");
        var original = File.ReadAllBytes(path);
        var second = await fixture.Inbox.SubmitAsync("Fence", new(Plan, null), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // Резервирование исключает второй план и обход через ручной клиент.
    [Fact]
    public async Task ReservationRejectsAnotherPlanAndManualCommand()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.ReserveAsync(Plan with { Id = new string('a', 32) }, CancellationToken.None));

        var manual = new PostgreSqlCommand(new string('b', 32), "Fence", Plan.From, Plan.To, Plan.SystemIdentifier, DateTimeOffset.UtcNow, null);

        Assert.Throws<InvalidOperationException>(() => PostgreSqlSwitchInbox.AssertCommandAllowed(fixture.Store.Root, manual));
    }

    // Без Reserve сервер не принимает фазу.
    [Fact]
    public async Task SubmissionRequiresReservation()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.SubmitAsync("Fence", new(Plan, null), CancellationToken.None));
    }

    // Отмена разрешена до первой поданной фазы.
    [Fact]
    public async Task CancellationIsAllowedOnlyBeforeSubmission()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);
        await fixture.Inbox.CancelAsync(Plan, CancellationToken.None);
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);
        await fixture.Inbox.SubmitAsync("Fence", new(Plan, null), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.CancelAsync(Plan, CancellationToken.None));
    }

    // Завершение требует подтверждённых локальных фаз.
    [Fact]
    public async Task CompletionIsRefusedBeforeRejoin()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.CompleteAsync(Plan, CancellationToken.None));
    }

    // Повтор Id с другим содержимым плана отвергается.
    [Fact]
    public async Task ChangedPlanCannotReuseAnExistingPhase()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);
        await fixture.Inbox.SubmitAsync("Fence", new(Plan, null), CancellationToken.None);

        var changed = Plan with { CreatedAtUtc = Plan.CreatedAtUtc.AddSeconds(1) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inbox.SubmitAsync("Fence", new(changed, null), CancellationToken.None));
    }

    // Сбой между записью Completed и удалением Active не оставляет вечную блокировку.
    [Fact]
    public async Task CompletionRetryClearsOnlyItsOwnReservation()
    {
        using var fixture = new Fixture();

        await fixture.StartAsync();
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);

        foreach (var mode in new[] { "Fence", "Rejoin" })
        {
            var directory = Path.Combine(fixture.Store.Root, Plan.OperationId(mode));

            Directory.CreateDirectory(directory);

            PostgreSqlCommandStore.Write(Path.Combine(directory, "request.json"), new PostgreSqlCommand(Plan.OperationId(mode), mode, Plan.From, Plan.To, Plan.SystemIdentifier, DateTimeOffset.UtcNow, null));
            PostgreSqlCommandStore.Write(Path.Combine(directory, "journal.json"), new PostgreSqlCommandJournal(Plan.OperationId(mode), "test", "Succeeded", DateTimeOffset.UtcNow, "Confirmed"));
            PostgreSqlCommandStore.Write(Path.Combine(directory, "script-result.json"), new { Success = true });

            if (mode == "Fence")
                PostgreSqlCommandStore.Write(Path.Combine(directory, "fence.json"), new { State = "Fenced" });
        }

        await fixture.Inbox.CompleteAsync(Plan, CancellationToken.None);

        var active = Path.Combine(fixture.Inbox.NetworkRoot, "active-switch.json");

        PostgreSqlCommandStore.Write(active, Plan);

        await fixture.Inbox.CompleteAsync(Plan, CancellationToken.None);

        Assert.False(File.Exists(active));

        await fixture.Inbox.ReserveAsync(Plan with { Id = new string('c', 32) }, CancellationToken.None);
    }

    // Pin не отменяет проверку имени сервера и EKU.
    [Fact]
    public void CertificatePinStillRequiresServerNameAndPurpose()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        Assert.True(PostgreSqlNodeCertificates.AcceptServer(certificate, pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(PostgreSqlNodeCertificates.AcceptServer(certificate, pin, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(PostgreSqlNodeCertificates.AcceptServer(certificate, new string('0', 64), SslPolicyErrors.None));
        Assert.False(PostgreSqlNodeCertificates.Matches(certificate, pin, "1.3.6.1.5.5.7.3.2"));
    }

    // Начальная проверка отвергает устаревшие данные и две пишущие БД.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialCheckRejectsStaleOrConflictingRoles(bool stale)
    {
        var source = Status(Plan.From, "Primary");
        var target = Status(Plan.To, stale ? "Standby" : "Primary");

        if (stale)
            target = target with { Local = target.Local with { CheckedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) } };

        Assert.Throws<InvalidOperationException>(() => PostgreSqlSwitchCoordinator.CheckInitial(Plan, source, target));
    }

    // Создаёт здоровый снимок для проверки протокола.
    private static PostgreSqlRemoteStatus Status(string address, string role)
    {
        return new(address, new() { Settings = new() { CommandsEnabled = true, ExpectedSystemIdentifier = Plan.SystemIdentifier }, CheckedAtUtc = DateTimeOffset.UtcNow, ControllerState = "Monitoring", OwnsController = true, PermissionsOk = true, Service = new() { ManagedByService = true }, SystemIdentifier = Plan.SystemIdentifier, PostgreSqlRole = role, Timeline = 2, HealthPeriodSeconds = 10 });
    }

    private sealed class Remote : IPostgreSqlSwitchRemote
    {
        public List<string> Calls = [];
        public string? Failed;
        public bool FailReservation;
        public bool WrongProof;
        public bool LoseFenceResponse;
        public bool QueueFence;
        public int FenceReadFailures;
        public bool MissingFenceProof;
        public bool WrongEmergencyId;
        public string? MissingPhase;
        public Func<string, PostgreSqlRemoteStatus>? StatusOverride;

        public Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct)
        {
            return Task.FromResult(StatusOverride?.Invoke(address) ?? Status(address, address == Plan.From ? "Primary" : "Standby"));
        }

        public Task PlanAsync(string address, string action, PostgreSqlSwitchPlan plan, CancellationToken ct)
        {
            Calls.Add(action + ":" + address);

            if (FailReservation && address == Plan.To && action == "reserve")
                throw new IOException("Peer refused reservation.");

            return Task.CompletedTask;
        }

        public Task<PostgreSqlRemoteOperation> SubmitAsync(string address, string mode, PostgreSqlPhaseRequest phase, CancellationToken ct)
        {
            Calls.Add(mode + ":" + address);

            if (mode != "Fence")
                Assert.NotNull(phase.Fence);

            if (mode == "Fence" && LoseFenceResponse)
                throw new HttpRequestException("Connection lost while waiting for Fence.");

            var operation = Operation(phase.Plan, mode);
            return Task.FromResult(mode == "Fence" && QueueFence ? operation with { State = "Queued", Fence = null } : operation);
        }

        private PostgreSqlRemoteOperation Operation(PostgreSqlSwitchPlan plan, string mode)
        {
            JsonElement? proof = mode == "Fence" && !MissingFenceProof
                ? JsonSerializer.SerializeToElement(new { State = "Fenced", plan.From, plan.To, Sid = WrongProof ? "123" : plan.SystemIdentifier, Format = plan.Emergency ? 2 : 1, Id = WrongEmergencyId ? Plan.OperationId("Fence") : plan.OperationId("Fence") })
                : null;

            return new PostgreSqlRemoteOperation(plan.OperationId(mode), mode == Failed ? "NeedsReview" : mode == MissingPhase ? "Missing" : "Succeeded", "Test", null, proof);
        }

        public Task<PostgreSqlRemoteOperation> ReadAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct)
        {
            Assert.Equal(mode == "Promote" ? plan.To : plan.From, address);
            Calls.Add("read-" + mode + ":" + address);

            if (mode == "Fence" && FenceReadFailures > 0)
            {
                FenceReadFailures--;
                throw new IOException("Temporary error reading the Fence journal.");
            }

            return Task.FromResult(Operation(plan, mode));
        }
    }

    private sealed class Fixture : IDisposable, IPostgreSqlServicePresence, IPostgreSqlLocalProbe
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public PostgreSqlCommandStore Store { get; }
        public PostgreSqlNodeMonitor Monitor { get; }
        public PostgreSqlSwitchInbox Inbox { get; }

        public Fixture()
        {
            var options = Options.Create(new PostgreSqlControlOptions { CommandsEnabled = true, ExpectedSystemIdentifier = Plan.SystemIdentifier });
            var leases = new PostgreSqlControllerLease(_root);

            Store = new(options, leases);
            Monitor = new(options, new ConfigurationBuilder().Build(), PostgreSqlHostKind.Runtime, this, this, leases, NullLogger<PostgreSqlNodeMonitor>.Instance);

            var network = Options.Create(new PostgreSqlNetworkOptions { Enabled = true, NodeAddress = Plan.From, PeerAddress = Plan.To, CertificateSha256 = new string('a', 64), PeerCertificateSha256 = new string('b', 64) });

            Inbox = new(Store, Monitor, Monitor, options, network);
        }

        public Task StartAsync()
        {
            return Monitor.RefreshAsync(CancellationToken.None);
        }

        public PostgreSqlAutomaticFailoverWorker CreateAutomaticWorker(int threshold)
        {
            var control = Options.Create(new PostgreSqlControlOptions { CommandsEnabled = true, ExpectedSystemIdentifier = Plan.SystemIdentifier });
            var network = Options.Create(new PostgreSqlNetworkOptions { Enabled = true, NodeAddress = Plan.To, PeerAddress = Plan.From, CertificateSha256 = new string('b', 64), PeerCertificateSha256 = new string('a', 64) });
            return new(Options.Create(new PostgreSqlAutomaticFailoverOptions { Enabled = true, FailureThreshold = threshold }), network, control, Monitor, Store, NullLogger<PostgreSqlAutomaticFailoverWorker>.Instance);
        }

        public PostgreSqlAutomaticFailbackWorker CreateFailbackWorker(int threshold)
        {
            var control = Options.Create(new PostgreSqlControlOptions { CommandsEnabled = true, ExpectedSystemIdentifier = Plan.SystemIdentifier });
            var network = Options.Create(new PostgreSqlNetworkOptions { Enabled = true, NodeAddress = Plan.From, PeerAddress = Plan.To });
            return new(Options.Create(new PostgreSqlAutomaticFailbackOptions { Enabled = true, PreferredPrimary = Plan.To, RecoveryThreshold = threshold }), network, control, Monitor, Store, NullLogger<PostgreSqlAutomaticFailbackWorker>.Instance);
        }

        public bool IsInstalled(string serviceName)
        {
            return true;
        }

        public Task<PostgreSqlLocalHealthResponse> CheckAsync(PostgreSqlControlOptions settings, int timeoutSeconds, CancellationToken ct)
        {
            return Task.FromResult(Status(Plan.From, "Primary").Local);
        }

        public void Dispose()
        {
            Monitor.Dispose();

            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}
