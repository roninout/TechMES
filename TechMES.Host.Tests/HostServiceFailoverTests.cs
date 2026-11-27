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
            new() { Enabled = true, RecoveryThreshold = 2 }, _ => Task.CompletedTask, NullLogger<HostServiceFailoverWorker>.Instance);
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
        var requests = 0;
        using var standby = new HostServiceFailoverWorker(pair.OwnerB, pair.ToA, pair.B.Settings,
            new() { Enabled = true, FailureThreshold = 2 }, _ => { requests++; return Task.CompletedTask; },
            NullLogger<HostServiceFailoverWorker>.Instance);

        await standby.ObserveAsync(CancellationToken.None);
        Assert.Equal(0, requests);
        await standby.ObserveAsync(CancellationToken.None);
        Assert.Equal(1, requests);
        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);

        // После подтверждения остановки Windows запускает тот же координатор с его журналом.
        await pair.OwnerA.TransferVipBeforeHostStopAsync();
        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
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
            new() { Enabled = true, RecoveryThreshold = 2 }, _ => Task.CompletedTask, NullLogger<HostServiceFailoverWorker>.Instance);
        await preferred.ObserveAsync(CancellationToken.None);
        await preferred.ObserveAsync(CancellationToken.None);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
    }
}
