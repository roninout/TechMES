using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;
using TechMES.PostgreSql.Control;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlMaintenanceTests
{
    private static readonly PostgreSqlSwitchPlan Plan = new("123456781234123412341234567890ab", "192.168.1.131", "192.168.1.132", "7703468038317098096", DateTimeOffset.UtcNow);

    // План сохраняется до первого запроса; новое окно продолжает именно его.
    [Fact]
    public async Task LostReservationResponseRetainsTheSamePlanForResume()
    {
        using var fixture = new Fixture();

        fixture.Remote.FailReserve = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Session().RunAsync(Plan.From, Plan.To, false, _ => { }, CancellationToken.None));

        var saved = PostgreSqlCommandStore.Read<PostgreSqlSwitchPlan>(Path.Combine(fixture.Store.Root, "Network", "console-active.json"));

        fixture.Remote.FailReserve = false;

        await fixture.Session().RunAsync(Plan.From, Plan.To, true, _ => { }, CancellationToken.None);

        Assert.All(fixture.Remote.Submitted, plan => Assert.Equal(saved, plan));
        Assert.False(File.Exists(Path.Combine(fixture.Store.Root, "Network", "console-active.json")));
    }

    // Новый клиент принимает существующее резервирование и не требует прежнего Primary online.
    [Fact]
    public async Task ResumeAdoptsServerPlanAfterFence()
    {
        using var fixture = new Fixture();

        fixture.Remote.Active = Plan;
        fixture.Remote.SourceRole = "Stopped";

        await fixture.Session().RunAsync(Plan.From, Plan.To, true, _ => { }, CancellationToken.None);

        Assert.All(fixture.Remote.Submitted, plan => Assert.Equal(Plan, plan));
    }

    // Существующий план запрещает создание нового даже при здоровой паре БД.
    [Fact]
    public async Task NewSwitchIsRejectedWhileAnotherPlanIsPending()
    {
        using var fixture = new Fixture();

        fixture.Remote.Active = Plan;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session().RunAsync(Plan.From, Plan.To, false, _ => { }, CancellationToken.None));

        Assert.Empty(fixture.Remote.Submitted);
    }

    // Неопределённая фаза сохраняет план и запрещает Promote.
    [Fact]
    public async Task NeedsReviewNeverAdvancesToPromote()
    {
        using var fixture = new Fixture();

        fixture.Remote.FailFence = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session().RunAsync(Plan.From, Plan.To, false, _ => { }, CancellationToken.None));

        Assert.Contains("NeedsReview", error.Message);
        Assert.Equal(new[] { "Fence" }, fixture.Remote.Modes);
        Assert.True(File.Exists(Path.Combine(fixture.Store.Root, "Network", "console-active.json")));
    }

    // Противоречащие резервирования не разрешаются выбором первого попавшегося.
    [Fact]
    public void ConflictingReservationsAreRejected()
    {
        var nodes = new[] { Status(Plan.From, "Primary") with { ActivePlan = Plan }, Status(Plan.To, "Standby") with { ActivePlan = Plan with { Id = new string('a', 32) } } };

        Assert.Throws<InvalidOperationException>(() => PostgreSqlMaintenanceSession.ResolvePending(null, nodes));
    }

    // История содержит исходную причину остановки, а чтение не меняет NeedsReview.
    [Fact]
    public async Task HistoryExposesFailureWithoutAcknowledgingOrRetryingIt()
    {
        using var fixture = new Fixture();

        await fixture.Monitor.RefreshAsync(CancellationToken.None);
        await fixture.Inbox.ReserveAsync(Plan, CancellationToken.None);

        var operation = await fixture.Inbox.SubmitAsync("Fence", new(Plan, null), CancellationToken.None);
        var root = Path.Combine(fixture.Store.Root, operation.Id);

        PostgreSqlCommandStore.Write(Path.Combine(root, "script-result.json"), JsonSerializer.SerializeToElement(new { Success = false, Error = "SQL probe failed", Log = "saved-switch.log" }));
        PostgreSqlCommandStore.Write(Path.Combine(root, "journal.json"), new PostgreSqlCommandJournal(operation.Id, "test", "NeedsReview", DateTimeOffset.UtcNow, "Completion was not confirmed."));

        var phase = fixture.Inbox.History().Single().Phases.Single(item => item.Mode == "Fence");

        Assert.Equal("NeedsReview", phase.State);
        Assert.Equal("SQL probe failed", phase.Error);
        Assert.Equal("saved-switch.log", phase.Log);
        Assert.Contains("SQL probe failed", phase.Message);
        Assert.Equal("NeedsReview", PostgreSqlCommandStore.Read<PostgreSqlCommandJournal>(Path.Combine(root, "journal.json")).State);
        Assert.Equal(Plan, fixture.Inbox.Status().ActivePlan);
    }

    // Доверие Maintenance задаётся отдельным pin и требует клиентский EKU.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MaintenanceCertificateRequiresItsOwnPinAndClientPurpose(bool clientPurpose)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=Maintenance", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(clientPurpose ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        var options = new PostgreSqlNetworkOptions { CertificateSha256 = new string('a', 64), PeerCertificateSha256 = new string('b', 64), MaintenanceCertificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256) };

        Assert.Equal(clientPurpose, PostgreSqlNodeCertificates.AcceptClient(certificate, options));
        Assert.False(PostgreSqlNodeCertificates.AcceptClient(certificate, options with { MaintenanceCertificateSha256 = "" }));
    }

    // Создаёт готовый снимок контроллера для проверки протокола без PostgreSQL.
    private static PostgreSqlRemoteStatus Status(string address, string role) => new(address, new() { Settings = new() { CommandsEnabled = true, ExpectedSystemIdentifier = Plan.SystemIdentifier }, CheckedAtUtc = DateTimeOffset.UtcNow, ControllerState = "Monitoring", OwnsController = true, PermissionsOk = true, Service = new() { ManagedByService = true }, SystemIdentifier = Plan.SystemIdentifier, PostgreSqlRole = role, Timeline = 2, HealthPeriodSeconds = 10 });

    private sealed class Remote : IPostgreSqlMaintenanceRemote
    {
        public bool FailReserve;
        public bool FailFence;
        public PostgreSqlSwitchPlan? Active;
        public string SourceRole = "Primary";
        public List<PostgreSqlSwitchPlan> Submitted = [];
        public List<string> Modes = [];

        public Task<PostgreSqlRemoteStatus> StatusAsync(string address, CancellationToken ct) => Task.FromResult(Status(address, address == Plan.From ? SourceRole : "Standby") with { ActivePlan = Active });
        public Task<PostgreSqlSwitchHistoryEntry[]> HistoryAsync(string address, CancellationToken ct) => Task.FromResult(Array.Empty<PostgreSqlSwitchHistoryEntry>());
        public Task<PostgreSqlRemoteOperation> ReadAsync(string address, string mode, PostgreSqlSwitchPlan plan, CancellationToken ct) => throw new NotImplementedException();

        public Task PlanAsync(string address, string action, PostgreSqlSwitchPlan plan, CancellationToken ct)
        {
            if (FailReserve && action == "reserve")
                throw new IOException("The reservation response was lost.");

            return Task.CompletedTask;
        }

        public Task<PostgreSqlRemoteOperation> SubmitAsync(string address, string mode, PostgreSqlPhaseRequest phase, CancellationToken ct)
        {
            Submitted.Add(phase.Plan);
            Modes.Add(mode);

            JsonElement? proof = mode == "Fence" ? JsonSerializer.SerializeToElement(new { State = "Fenced", From = phase.Plan.From, To = phase.Plan.To, Sid = phase.Plan.SystemIdentifier }) : null;

            return Task.FromResult(new PostgreSqlRemoteOperation(phase.Plan.OperationId(mode), FailFence && mode == "Fence" ? "NeedsReview" : "Succeeded", "Saved result", null, proof));
        }
    }

    private sealed class Fixture : IDisposable, IPostgreSqlServicePresence, IPostgreSqlLocalProbe
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly PostgreSqlControlOptions _control = new() { CommandsEnabled = true, ExpectedSystemIdentifier = Plan.SystemIdentifier };
        private readonly PostgreSqlNetworkOptions _network = new() { Enabled = true, NodeAddress = Plan.From, PeerAddress = Plan.To, CertificateSha256 = new string('a', 64), PeerCertificateSha256 = new string('b', 64) };

        public Remote Remote { get; } = new();
        public PostgreSqlCommandStore Store { get; }
        public PostgreSqlNodeMonitor Monitor { get; }
        public PostgreSqlSwitchInbox Inbox { get; }

        public Fixture()
        {
            var options = Options.Create(_control);
            var leases = new PostgreSqlControllerLease(_root);

            Store = new(options, leases);
            Monitor = new(options, new ConfigurationBuilder().Build(), PostgreSqlHostKind.Runtime, this, this, leases, NullLogger<PostgreSqlNodeMonitor>.Instance);
            Inbox = new(Store, Monitor, Monitor, options, Options.Create(_network));
        }

        public PostgreSqlMaintenanceSession Session() => new(Remote, Store, _network, _control);
        public bool IsInstalled(string serviceName) => true;
        public Task<PostgreSqlLocalHealthResponse> CheckAsync(PostgreSqlControlOptions settings, int timeoutSeconds, CancellationToken ct) => Task.FromResult(Status(Plan.From, "Primary").Local);

        public void Dispose()
        {
            Monitor.Dispose();

            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}