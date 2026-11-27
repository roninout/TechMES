using Microsoft.Extensions.Logging.Abstractions;

namespace TechMES.Host.Tests;

public sealed partial class HostOwnerTests
{
    [Fact]
    public async Task StoppingPreferredHostHandsOffAndItReturnsOnlyWhenWebHasRecovered()
    {
        using var pair = new Pair();
        await pair.Change(pair.A, true);
        pair.Events.Clear();

        await pair.OwnerA.TransferVipBeforeHostStopAsync();
        await pair.OwnerA.StopAsync();
        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.False(pair.OwnerA.Status().Grant.Assigned);

        pair.Restart();
        await pair.OwnerA.ReconcileAsync();
        Assert.False(pair.A.Present);

        using var preferred = new HostServiceFailoverWorker(pair.OwnerA, pair.ToB, pair.A.Settings,
            new() { Enabled = true, RecoveryThreshold = 2 }, null, NullLogger<HostServiceFailoverWorker>.Instance);
        pair.A.Ready = false;
        await preferred.ObserveAsync(CancellationToken.None);
        await preferred.ObserveAsync(CancellationToken.None);
        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);

        pair.A.Ready = true;
        pair.Events.Clear();
        await preferred.ObserveAsync(CancellationToken.None);
        Assert.True(pair.B.Present);
        await preferred.ObserveAsync(CancellationToken.None);

        Assert.Equal(new[] { "B:False", "A:True" }, pair.Events);
        Assert.False(pair.B.Present);
        Assert.True(pair.A.Present);
    }

    [Fact]
    public async Task UnreachablePreferredHostNeverLetsTheStandbyGrantItself()
    {
        using var pair = new Pair();
        await pair.Change(pair.A, true);
        pair.ToA.Offline = true;
        var requests = new List<HostRecoveryAction>();
        using var standby = new HostServiceFailoverWorker(pair.OwnerB, pair.ToA, pair.B.Settings,
            new() { Enabled = true, FailureThreshold = 2 }, (action, _) =>
            {
                requests.Add(action);
                return Task.CompletedTask;
            },
            NullLogger<HostServiceFailoverWorker>.Instance);

        await standby.ObserveAsync(CancellationToken.None);
        Assert.Empty(requests);
        await standby.ObserveAsync(CancellationToken.None);
        Assert.Equal(HostRecoveryAction.HandoffVip, Assert.Single(requests));
        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);

        // После подтверждения остановки Windows запускает тот же координатор с его журналом.
        await pair.OwnerA.TransferVipBeforeHostStopAsync();
        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
    }

    [Fact]
    public async Task StandbyWithVipRequestsPreferredHostStartOnlyAfterWebFails()
    {
        using var pair = new Pair(automatic: true);
        await pair.Change(pair.A, true);
        await pair.OwnerA.TransferVipBeforeHostStopAsync();
        await pair.OwnerA.StopAsync();
        pair.ToA.Offline = true;
        pair.Events.Clear();

        var requests = new List<HostRecoveryAction>();
        using var standby = new HostServiceFailoverWorker(pair.OwnerB, pair.ToA, pair.B.Settings,
            new() { Enabled = true, FailureThreshold = 2 }, (action, _) =>
            {
                requests.Add(action);
                return Task.CompletedTask;
            }, NullLogger<HostServiceFailoverWorker>.Instance);

        await standby.ObserveAsync(CancellationToken.None);
        await standby.ObserveAsync(CancellationToken.None);
        Assert.Empty(requests);

        pair.B.Ready = false;
        await standby.ObserveAsync(CancellationToken.None);
        pair.B.Ready = true;
        await standby.ObserveAsync(CancellationToken.None);
        pair.B.Ready = false;
        await standby.ObserveAsync(CancellationToken.None);
        Assert.Empty(requests);
        await standby.ObserveAsync(CancellationToken.None);

        Assert.Equal(HostRecoveryAction.StartPreferredHost, Assert.Single(requests));
        Assert.Empty(pair.Events);
        Assert.True(pair.B.Present);

        // Координатор снова запущен. Обычная политика WEB возвращает VIP.
        pair.Restart();
        pair.ToA.Offline = false;
        for (var i = 0; i < pair.WebFailover.FailureThreshold; i++)
            await pair.OwnerA.CheckWebFailoverAsync();

        var coordinator = pair.OwnerA.Status();
        Assert.True(coordinator.Journal.AutomaticWebFailover);
        Assert.Equal(pair.A.Settings.NodeAddress, coordinator.Journal.Owner);
        Assert.Equal(new[] { "B:False", "A:True" }, pair.Events);
        Assert.True(coordinator.AddressReady);
        Assert.False(pair.OwnerB.Status().AddressPresent);
    }

    [Fact]
    public async Task HostRecoveryDoesNotReturnVipMovedByTheExistingWebPolicy()
    {
        using var pair = new Pair(true);
        await pair.Change(pair.A, true);
        pair.A.Ready = false;
        for (var i = 0; i < 4; i++)
            await pair.OwnerA.CheckWebFailoverAsync();
        Assert.True(pair.B.Present);

        pair.A.Ready = true;
        using var preferred = new HostServiceFailoverWorker(pair.OwnerA, pair.ToB, pair.A.Settings,
            new() { Enabled = true, RecoveryThreshold = 2 }, null, NullLogger<HostServiceFailoverWorker>.Instance);
        await preferred.ObserveAsync(CancellationToken.None);
        await preferred.ObserveAsync(CancellationToken.None);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
    }
}
