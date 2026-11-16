using TechMES.Contracts.Host;

namespace TechMES.Host.Tests;

public sealed class HostOwnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAndTransferNeverOverlap(bool startOnPeer)
    {
        using var pair = new Pair();

        await pair.Change(startOnPeer ? pair.B : pair.A, true);
        pair.Events.Clear();
        await pair.Change(startOnPeer ? pair.A : pair.B, false);

        Assert.Equal(new[] { (startOnPeer ? "B" : "A") + ":False", (startOnPeer ? "A" : "B") + ":True" }, pair.Events);
        Assert.NotEqual(pair.A.Present, pair.B.Present);
        Assert.Equal("Stable", pair.OwnerA.Status().Journal.Phase);
    }

    [Fact]
    public async Task LostReleaseAckDoesNotGrantTargetAndCanResume()
    {
        using var pair = new Pair();

        await pair.Change(pair.B, true);
        pair.ToB.LoseReply = true;

        await Assert.ThrowsAsync<IOException>(() => pair.Change(pair.A, false));

        Assert.False(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Equal("Releasing", pair.OwnerA.Status().Journal.Phase);

        pair.Restart();
        await pair.OwnerA.ResumeAsync(pair.OwnerA.Status().Journal.Operation);

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
    }

    [Fact]
    public async Task LostGrantAckKeepsSingleOwnerAndResumeIsIdempotent()
    {
        using var pair = new Pair();

        pair.ToB.LoseReply = true;

        await Assert.ThrowsAsync<IOException>(() => pair.Change(pair.B, true));

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal("Granting", pair.OwnerA.Status().Journal.Phase);

        pair.Restart();

        var operation = pair.OwnerA.Status().Journal.Operation;

        await pair.OwnerA.ResumeAsync(operation);
        await pair.OwnerA.ResumeAsync(operation);

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
    }

    [Fact]
    public async Task FailedReleaseNeverGrantsNewOwner()
    {
        using var pair = new Pair();

        await pair.Change(pair.B, true);
        pair.B.FailRemove = true;

        await Assert.ThrowsAsync<IOException>(() => pair.Change(pair.A, false));

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal("Releasing", pair.OwnerA.Status().Journal.Phase);

        pair.B.FailRemove = false;
        await pair.OwnerA.ResumeAsync(pair.OwnerA.Status().Journal.Operation);

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
    }

    [Fact]
    public async Task OfflinePeerBlocksNewOperation()
    {
        using var pair = new Pair();

        pair.ToB.Offline = true;

        await Assert.ThrowsAsync<IOException>(() => pair.Change(pair.A, true));

        Assert.Equal("Empty", pair.OwnerA.Status().Journal.Phase);
        Assert.False(pair.A.Present);
    }

    [Fact]
    public async Task StaleEpochCannotReturnIpToOldOwner()
    {
        using var pair = new Pair();

        await pair.Change(pair.A, true);
        var old = pair.OwnerA.Status();

        await pair.Change(pair.B, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.OwnerA.ApplyAsync(new(old.Pair, old.Grant.Epoch, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.OwnerA.ChangeAsync(new(pair.A.Settings.NodeAddress, old.Journal.Epoch, Guid.NewGuid()), false));

        Assert.False(pair.A.Present);
        Assert.True(pair.B.Present);
    }

    [Fact]
    public async Task RebootRestoresOnlyGrantedOwner()
    {
        using var pair = new Pair();

        await pair.Change(pair.B, true);
        pair.B.Present = false;

        pair.Restart();

        await pair.OwnerA.ReconcileAsync();
        await pair.OwnerB.ReconcileAsync();

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
    }

    [Fact]
    public async Task CoordinatorOutageDoesNotAllowPeerToClaim()
    {
        using var pair = new Pair();

        await pair.Change(pair.A, true);
        pair.ToA.Offline = true;

        await pair.OwnerB.ReconcileAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.OwnerB.ChangeAsync(new(pair.B.Settings.NodeAddress, 2, Guid.NewGuid()), false));

        Assert.False(pair.B.Present);
        Assert.True(pair.A.Present);
    }

    [Fact]
    public async Task TargetNeedsReadyWebAndStaticNetwork()
    {
        using var pair = new Pair();

        pair.B.Ready = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Change(pair.B, true));

        pair.B.Ready = true;
        pair.B.Dhcp = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Change(pair.B, true));

        Assert.False(pair.A.Present);
        Assert.False(pair.B.Present);
    }

    [Fact]
    public async Task ShutdownRemovesIpButPreservesGrant()
    {
        using var pair = new Pair();

        await pair.Change(pair.A, true);
        await pair.OwnerA.StopAsync();

        Assert.False(pair.A.Present);
        Assert.True(pair.OwnerA.Status().Grant.Assigned);

        pair.Restart();
        await pair.OwnerA.ReconcileAsync();

        Assert.True(pair.A.Present);
    }

    [Fact]
    public async Task ExistingForeignVipIsNeverAdoptedOrRemoved()
    {
        using var pair = new Pair();

        pair.B.Present = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Change(pair.B, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.OwnerB.ApplyAsync(new(pair.OwnerB.Status().Pair, 1, false)));

        Assert.True(pair.B.Present);
    }

    [Fact]
    public void CorruptOrChangedProfileJournalIsNotReset()
    {
        using var pair = new Pair();

        pair.StoreA.Dispose();

        Assert.Throws<InvalidDataException>(() => new HostOwnerStore(pair.FolderA, "wrong pair", pair.A.Settings.InterfaceId, false));

        File.WriteAllText(Path.Combine(pair.FolderA, "ownership.jsonl"), "{");

        Assert.ThrowsAny<Exception>(() => pair.Restart());
    }

    [Fact]
    public async Task WrongPairCannotApplyGrant()
    {
        using var pair = new Pair();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.OwnerB.ApplyAsync(new("wrong pair", 2, true)));

        Assert.False(pair.B.Present);
    }

    [Fact]
    public void SecondProcessCannotOpenJournal()
    {
        using var pair = new Pair();

        Assert.Throws<IOException>(() => new HostOwnerStore(pair.FolderA, "test-pair", pair.A.Settings.InterfaceId, false));
    }

    [Fact]
    public async Task JournalWriteFailureCannotAssignAndLatchesFailure()
    {
        using var pair = new Pair();

        var path = Path.Combine(pair.FolderA, "ownership.jsonl");

        File.Delete(path);
        Directory.CreateDirectory(path);

        await Assert.ThrowsAnyAsync<Exception>(() => pair.Change(pair.A, true));

        Assert.False(pair.A.Present);
        Assert.False(pair.B.Present);
        Assert.Throws<IOException>(() => pair.OwnerA.Status());
    }

    [Fact]
    public async Task ConcurrentOperationIsRejected()
    {
        using var pair = new Pair();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ToB.Delay = release.Task;

        var first = pair.Change(pair.A, true);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Change(pair.B, true));
        }
        finally
        {
            release.SetResult();
        }

        await first;

        Assert.True(pair.A.Present);
        Assert.False(pair.B.Present);
    }

    [Fact]
    public async Task DuplicateOperationDoesNotIncreaseEpoch()
    {
        using var pair = new Pair();

        var request = new HostOwnerRequest(pair.B.Settings.NodeAddress, 0, Guid.NewGuid());

        var first = await pair.OwnerA.ChangeAsync(request, true);
        var duplicate = await pair.OwnerA.ChangeAsync(request, true);

        Assert.Equal(first.Journal.Epoch, duplicate.Journal.Epoch);
        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
    }

    [Fact]
    public async Task RetryStillVerifiesRemovalWhenSnapshotCannotSeeVip()
    {
        using var pair = new Pair();

        await pair.Change(pair.B, true);

        pair.B.HideVip = true;
        pair.B.FailRemove = true;

        await Assert.ThrowsAsync<IOException>(() => pair.Change(pair.A, false));
        await Assert.ThrowsAsync<IOException>(() => pair.OwnerA.ResumeAsync(pair.OwnerA.Status().Journal.Operation));

        Assert.True(pair.B.Present);
        Assert.False(pair.A.Present);
        Assert.Equal("Releasing", pair.OwnerA.Status().Journal.Phase);
    }

    private sealed class Pair : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "techmes-owner-" + Guid.NewGuid().ToString("N"));

        public string FolderA => Path.Combine(_root, "A");
        public List<string> Events { get; } = [];

        public FakeVip A { get; }
        public FakeVip B { get; }

        public FakePeer ToA { get; } = new();
        public FakePeer ToB { get; } = new();

        public HostOwnerStore StoreA { get; private set; } = null!;
        public HostOwnerStore StoreB { get; private set; } = null!;

        public HostOwnerEngine OwnerA { get; private set; } = null!;
        public HostOwnerEngine OwnerB { get; private set; } = null!;

        public Pair()
        {
            A = new("A", "10.20.30.11", "10.20.30.12", Events);
            B = new("B", "10.20.30.12", "10.20.30.11", Events);

            Restart();
        }

        public void Restart()
        {
            StoreA?.Dispose();
            StoreB?.Dispose();

            const string pair = "test-pair";

            StoreA = new(FolderA, pair, A.Settings.InterfaceId, false);
            StoreB = new(Path.Combine(_root, "B"), pair, B.Settings.InterfaceId, false);

            OwnerA = new(A.Settings, pair, StoreA, A, ToB, () => A.Ready);
            OwnerB = new(B.Settings, pair, StoreB, B, ToA, () => B.Ready);

            ToA.Owner = OwnerA;
            ToB.Owner = OwnerB;
        }

        public Task<HostOwnerStatus> Change(FakeVip target, bool initialize) =>
            OwnerA.ChangeAsync(new(target.Settings.NodeAddress, OwnerA.Status().Journal.Epoch, Guid.NewGuid()), initialize);

        public void Dispose()
        {
            StoreA?.Dispose();
            StoreB?.Dispose();

            Directory.Delete(_root, true);
        }
    }

    private sealed class FakePeer : IHostOwnerPeer
    {
        public HostOwnerEngine Owner { get; set; } = null!;

        public bool Offline { get; set; }
        public bool LoseReply { get; set; }
        public Task? Delay { get; set; }

        public async Task<HostOwnerStatus> StatusAsync()
        {
            if (Offline)
                throw new IOException("Peer offline");

            if (Delay is not null)
                await Delay;

            return Owner.Status();
        }

        public async Task ApplyAsync(HostApplyGrant command)
        {
            if (Offline)
                throw new IOException("Peer offline");

            await Owner.ApplyAsync(command);

            if (LoseReply)
            {
                LoseReply = false;
                throw new IOException("Reply lost");
            }
        }
    }

    private sealed class FakeVip(string name, string node, string peer, List<string> events) : IHostVip
    {
        public HostSharedAddressSettings Settings { get; } = new()
        {
            InterfaceId = "6d81b84e-6e80-40ea-8a93-6c89a5fbc034",
            NodeAddress = node,
            PeerAddress = peer,
            VirtualAddress = "10.20.30.200",
            PrefixLength = 24
        };

        public bool Present { get; set; }
        public bool HideVip { get; set; }
        public bool Ready { get; set; } = true;
        public bool Dhcp { get; set; }
        public bool FailRemove { get; set; }

        public HostNetworkSnapshot Read() => new(
            name,
            DateTimeOffset.UtcNow,
            [new(Settings.InterfaceId, "Ethernet", true, Dhcp, Present && !HideVip ? [new(node, 24), new(Settings.VirtualAddress, 24)] : [new(node, 24)], "")],
            "");

        public Task SetAsync(bool assigned)
        {
            if (!assigned && FailRemove)
                throw new IOException("Remove failed");

            Present = assigned;
            events.Add(name + ":" + assigned);

            return Task.CompletedTask;
        }
    }
}