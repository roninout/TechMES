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
    private static readonly PostgreSqlSwitchPlan Plan = new("123456781234123412341234567890ab", "192.168.1.131", "192.168.1.132", "7703468038317098096", DateTimeOffset.UtcNow);

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

        Assert.Equal(new[] { "reserve:192.168.1.131", "reserve:192.168.1.132", "Fence:192.168.1.131", "Promote:192.168.1.132", "Rejoin:192.168.1.131", "complete:192.168.1.131", "complete:192.168.1.132" }, remote.Calls);
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

        public Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct)
        {
            return Task.FromResult(Status(address, address == Plan.From ? "Primary" : "Standby"));
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

            JsonElement? proof = mode == "Fence" ? JsonSerializer.SerializeToElement(new { State = "Fenced", From = Plan.From, To = Plan.To, Sid = WrongProof ? "123" : Plan.SystemIdentifier }) : null;

            return Task.FromResult(new PostgreSqlRemoteOperation(Plan.OperationId(mode), mode == Failed ? "NeedsReview" : "Succeeded", "Test", null, proof));
        }

        public Task<PostgreSqlRemoteOperation> ReadAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct)
        {
            throw new NotImplementedException();
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