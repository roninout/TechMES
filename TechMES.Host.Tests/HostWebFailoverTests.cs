using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TechMES.Contracts.Host;

namespace TechMES.Host.Tests;

public sealed partial class HostOwnerTests
{
    [Fact]
    public async Task DisabledAutomationKeepsExistingOwner()
    {
        using var pair = new Pair();

        await pair.Change(pair.B, true);
        pair.B.Ready = false;

        await Observe(pair, 8);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal(2, pair.OwnerA.Status().Journal.Epoch);
        Assert.Equal("Disabled", pair.OwnerA.WebFailoverStatus.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebFailureTransfersBothDirectionsWithoutOverlapOrFailback(bool startOnPeer)
    {
        using var pair = new Pair(true);

        var previous = startOnPeer ? pair.B : pair.A;
        var target = startOnPeer ? pair.A : pair.B;

        await pair.Change(previous, true);
        pair.Events.Clear();
        previous.Ready = false;

        await Observe(pair, 4);

        Assert.Equal(new[] { (startOnPeer ? "B" : "A") + ":False", (startOnPeer ? "A" : "B") + ":True" }, pair.Events);
        Assert.True(target.Present);
        Assert.False(previous.Present);
        Assert.Equal(4, pair.OwnerA.Status().Journal.Epoch);
        Assert.True(pair.OwnerA.Status().Journal.AutomaticWebFailover);

        previous.Ready = true;

        await Observe(pair, 8);

        Assert.True(target.Present);
        Assert.False(previous.Present);
        Assert.Equal(4, pair.OwnerA.Status().Journal.Epoch);
    }

    [Fact]
    public async Task FollowerCannotCreateAutomaticGrant()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.A, true);
        pair.A.Ready = false;

        for (var i = 0; i < 8; i++)
            await pair.OwnerB.CheckWebFailoverAsync();

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Equal("Follower", pair.OwnerB.WebFailoverStatus.State);
    }

    [Fact]
    public async Task TransientFailureDoesNotTransfer()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.B.Ready = false;

        await Observe(pair, 3);

        pair.B.Ready = true;

        await Observe(pair, 4);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal(0, pair.OwnerA.WebFailoverStatus.OwnerFailures);
        Assert.Equal(2, pair.OwnerA.Status().Journal.Epoch);
    }

    [Fact]
    public async Task TargetMustHaveConsecutiveReadyObservations()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.A.Ready = false;
        pair.B.Ready = false;

        await Observe(pair, 5);

        Assert.True(pair.B.Present);

        pair.A.Ready = true;

        await Observe(pair, 2);

        Assert.True(pair.B.Present);

        pair.A.Ready = false;

        await Observe(pair, 1);

        pair.A.Ready = true;

        await Observe(pair, 2);

        Assert.True(pair.B.Present);

        await Observe(pair, 1);

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
    }

    [Fact]
    public async Task PartitionResetsObservationsAndDoesNotTakeOver()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.B.Ready = false;

        await Observe(pair, 3);

        pair.ToB.Offline = true;

        await Observe(pair, 6);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal("Blocked", pair.OwnerA.WebFailoverStatus.State);
        Assert.Equal(0, pair.OwnerA.WebFailoverStatus.OwnerFailures);

        pair.ToB.Offline = false;

        await Observe(pair, 3);

        Assert.True(pair.B.Present);

        await Observe(pair, 1);

        Assert.True(pair.A.Present);
    }

    [Fact]
    public async Task InvalidTargetNetworkBlocksAutomaticTransfer()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.B.Ready = false;
        pair.A.Dhcp = true;

        await Observe(pair, 8);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal("Blocked", pair.OwnerA.WebFailoverStatus.State);
    }

    [Fact]
    public async Task OwnerRecoveryIsRecheckedBeforeStartingTransfer()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.B.Ready = false;

        var reads = 0;

        pair.ToB.AfterRead = () =>
        {
            if (++reads == 4)
                pair.B.Ready = true;
        };

        await Observe(pair, 4);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal(2, pair.OwnerA.Status().Journal.Epoch);
    }

    [Fact]
    public async Task LocalRecoveryDuringPeerRequestPreventsStartingTransfer()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.A, true);
        pair.A.Ready = false;

        var reads = 0;

        pair.ToB.AfterRead = () =>
        {
            if (++reads == 5)
                pair.A.Ready = true;
        };

        await Observe(pair, 4);

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Equal(2, pair.OwnerA.Status().Journal.Epoch);
    }

    [Fact]
    public async Task FailedRemovalCannotAssignTargetAndCanResumeSameOperation()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.B.Ready = false;
        pair.B.FailRemove = true;

        await Observe(pair, 4);

        var pending = pair.OwnerA.Status().Journal;

        Assert.Equal("Releasing", pending.Phase);
        Assert.True(pending.AutomaticWebFailover);
        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);

        pair.B.FailRemove = false;

        await Observe(pair, 1);

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Equal(pending.Operation, pair.OwnerA.Status().Journal.Operation);
        Assert.Equal("Stable", pair.OwnerA.Status().Journal.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAutomaticAckResumesAfterRestart(bool lostGrantAck)
    {
        using var pair = new Pair(true);

        var previous = lostGrantAck ? pair.A : pair.B;

        await pair.Change(previous, true);
        previous.Ready = false;
        pair.ToB.LoseReply = true;

        await Observe(pair, 4);

        var pending = pair.OwnerA.Status().Journal;

        Assert.Equal(lostGrantAck ? "Granting" : "Releasing", pending.Phase);
        Assert.True(pending.AutomaticWebFailover);
        Assert.False(pair.A.Present && pair.B.Present);

        pair.Restart();

        await Observe(pair, 1);

        var completed = pair.OwnerA.Status().Journal;

        Assert.Equal("Stable", completed.Phase);
        Assert.Equal(pending.Operation, completed.Operation);
        Assert.Equal(4, completed.Epoch);
        Assert.Equal(lostGrantAck, pair.B.Present);
        Assert.Equal(!lostGrantAck, pair.A.Present);
    }

    [Fact]
    public async Task AutomationDoesNotResumeManualOperation()
    {
        using var pair = new Pair(true);

        pair.ToB.LoseReply = true;

        await Assert.ThrowsAsync<IOException>(() => pair.Change(pair.B, true));

        await Observe(pair, 4);

        Assert.Equal("Granting", pair.OwnerA.Status().Journal.Phase);
        Assert.False(pair.OwnerA.Status().Journal.AutomaticWebFailover);
        Assert.Equal("ManualOperation", pair.OwnerA.WebFailoverStatus.State);
    }

    [Fact]
    public async Task ChangedEpochDiscardsPreviousFailureStreak()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);
        pair.B.Ready = false;

        await Observe(pair, 3);
        await pair.Change(pair.A, false);

        pair.A.Ready = false;
        pair.B.Ready = true;

        await Observe(pair, 1);

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Equal(1, pair.OwnerA.WebFailoverStatus.OwnerFailures);
        Assert.Equal(4, pair.OwnerA.Status().Journal.Epoch);
    }

    [Fact]
    public async Task AutomationDoesNotInitializeUnusedPair()
    {
        using var pair = new Pair(true);

        await Observe(pair, 8);

        Assert.False(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Equal("Empty", pair.OwnerA.Status().Journal.Phase);
        Assert.Equal("NotInitialized", pair.OwnerA.WebFailoverStatus.State);
    }

    [Fact]
    public async Task LegacyJournalWithoutAutomaticMarkerStillLoads()
    {
        using var pair = new Pair(true);

        await pair.Change(pair.B, true);

        var legacy = JsonNode.Parse(JsonSerializer.Serialize(pair.StoreA.Current))!;

        legacy["Journal"]!.AsObject().Remove("AutomaticWebFailover");

        var payload = legacy.ToJsonString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        pair.StoreA.Dispose();

        File.WriteAllText(Path.Combine(pair.FolderA, "ownership.jsonl"), JsonSerializer.Serialize(new { Payload = payload, Sha256 = hash }) + "\n");

        pair.Restart();

        Assert.False(pair.OwnerA.Status().Journal.AutomaticWebFailover);
        Assert.Equal(2, pair.OwnerA.Status().Journal.Epoch);
        Assert.Equal(pair.B.Settings.NodeAddress, pair.OwnerA.Status().Journal.Owner);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(61, 3)]
    [InlineData(4, 1)]
    [InlineData(4, 61)]
    public void InvalidWebFailoverThresholdsAreRejected(int failures, int recovery)
    {
        var settings = new HostWebFailoverSettings { FailureThreshold = failures, RecoveryThreshold = recovery };

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public async Task ManagedOwnerCanValidateUnchangedNetworkForSettingsSave()
    {
        using var pair = new Pair();

        await pair.Change(pair.A, true);

        var check = pair.OwnerA.CheckNetwork(pair.A.Settings);

        Assert.True(check.LocalChecksPassed);
        Assert.True(pair.A.Present);
        Assert.Contains(check.Network.Adapters.SelectMany(adapter => adapter.Addresses), address => address.Address == pair.A.Settings.VirtualAddress);
    }

    [Fact]
    public void UnmanagedVipStillBlocksSettingsSave()
    {
        using var pair = new Pair();

        pair.A.Present = true;

        Assert.False(pair.OwnerA.CheckNetwork(pair.A.Settings).LocalChecksPassed);
        Assert.True(pair.A.Present);
    }

    [Fact]
    public async Task ChangedNetworkWithExistingVipStillBlocksSettingsSave()
    {
        using var pair = new Pair();

        await pair.Change(pair.A, true);

        var request = new HostSharedAddressSettings { InterfaceId = pair.A.Settings.InterfaceId, NodeAddress = pair.A.Settings.NodeAddress, PeerAddress = pair.A.Settings.PeerAddress, VirtualAddress = pair.A.Settings.VirtualAddress, PrefixLength = 25 };

        Assert.False(pair.OwnerA.CheckNetwork(request).LocalChecksPassed);
        Assert.True(pair.A.Present);
    }

    [Fact]
    public async Task MisconfiguredManagedVipStillBlocksSettingsSave()
    {
        using var pair = new Pair();

        await pair.Change(pair.A, true);
        pair.A.VipPrefixLength = 25;

        Assert.False(pair.OwnerA.CheckNetwork(pair.A.Settings).LocalChecksPassed);
        Assert.True(pair.A.Present);
    }

    private static async Task Observe(Pair pair, int count)
    {
        for (var i = 0; i < count; i++)
            await pair.OwnerA.CheckWebFailoverAsync();
    }
}