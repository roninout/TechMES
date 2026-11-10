using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;
using TechMES.PostgreSql.Control;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlNodeHostTests
{
    // Без локальной БД Runtime не создаёт владельца контроллера.
    [Fact]
    public async Task RuntimeWithoutPostgreSqlDoesNotTakeOwnership()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Runtime);

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal("NotInstalled", scenario.Monitor.GetHealth().ControllerState);
        Assert.False(scenario.Monitor.GetHealth().OwnsController);
        Assert.Equal(0, scenario.Probe.Calls);
    }

    // Автообнаружение включает общий модуль при установленной БД.
    [Fact]
    public async Task RuntimeAutomaticallyMonitorsInstalledDatabase()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Runtime);

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        var health = scenario.Monitor.GetHealth();

        Assert.Equal("Monitoring", health.ControllerState);
        Assert.Equal("Runtime", health.HostKind);
        Assert.True(health.OwnsController);
        Assert.True(health.MonitoringEnabled);
        Assert.False(health.Settings.Enabled);
        Assert.Equal("Standby", health.PostgreSqlRole);
        Assert.True(File.Exists(health.SnapshotPath));
    }

    // Отдельный хост работает на компьютере БД без Runtime.
    [Fact]
    public async Task DedicatedDatabaseUsesStandaloneHost()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Standalone);

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        Assert.True(scenario.Monitor.GetHealth().OwnsController);
        Assert.Equal("Standalone", scenario.Monitor.GetHealth().HostKind);
    }

    // Установленный Runtime имеет приоритет даже при временной остановке.
    [Fact]
    public async Task StandaloneDefersToInstalledRuntime()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Standalone);

        scenario.Presence.Names.UnionWith(["postgresql-x64-18", "TechMES.Runtime.Service"]);

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal("UseRuntime", scenario.Monitor.GetHealth().ControllerState);
        Assert.False(scenario.Monitor.GetHealth().OwnsController);
        Assert.Equal(0, scenario.Probe.Calls);
    }

    // Работающий отдельный хост уступает при появлении Runtime.
    [Fact]
    public async Task StandaloneReleasesOwnershipWhenRuntimeIsInstalled()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Standalone);

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        scenario.Presence.Names.Add("TechMES.Runtime.Service");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal("UseRuntime", scenario.Monitor.GetHealth().ControllerState);

        using var replacement = scenario.Leases.TryAcquire("POSTGRESQL-X64-18");

        Assert.NotNull(replacement);
    }

    // Файловая блокировка исключает второго владельца и освобождается.
    [Fact]
    public void FileLeaseHasOneOwnerAndIsReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var first = new PostgreSqlControllerLease(root);
            var second = new PostgreSqlControllerLease(root);

            using (var held = first.TryAcquire("postgresql-x64-18"))
            {
                Assert.NotNull(held);
                Assert.Null(second.TryAcquire("POSTGRESQL-X64-18"));
            }

            using var next = second.TryAcquire("postgresql-x64-18");

            Assert.NotNull(next);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Штатная остановка публикует Stopped и освобождает владение.
    [Fact]
    public async Task StopPublishesStoppedAndReleasesLease()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Standalone);

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        var path = scenario.Monitor.GetHealth().SnapshotPath!;

        await scenario.Monitor.StopAsync(CancellationToken.None);

        Assert.False(scenario.Monitor.GetHealth().OwnsController);
        Assert.Contains("Stopped", await File.ReadAllTextAsync(path));

        using var replacement = scenario.Leases.TryAcquire("postgresql-x64-18");

        Assert.NotNull(replacement);
    }

    // Runtime использует существующий health worker; отдельный хост получает свой цикл.
    [Theory]
    [InlineData(PostgreSqlHostKind.Runtime, 2)]
    [InlineData(PostgreSqlHostKind.Standalone, 3)]
    public void RegistrationDoesNotAddSecondRuntimeTimer(PostgreSqlHostKind kind, int expected)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddPostgreSqlNode(new ConfigurationBuilder().Build(), kind);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetServices<IHostedService>().Count());
    }

    // Явное отключение не запускает диагностику и не получает владение.
    [Fact]
    public async Task ExplicitDisableDoesNotProbeOrTakeLease()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Runtime, new() { AutoDetect = false });

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal("Disabled", scenario.Monitor.GetHealth().ControllerState);
        Assert.Equal(0, scenario.Probe.Calls);
    }

    // Локальная реплика обслуживается независимо от активности приложений.
    [Fact]
    public async Task StandbyDatabaseOwnerCanExecuteWithFreshClusterCheck()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Runtime, new() { CommandsEnabled = true, ExpectedSystemIdentifier = "7703468038317098096" });

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        var executed = false;

        await scenario.Monitor.ExecuteOwnedAsync(_ => { executed = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(executed);
        Assert.Equal(2, scenario.Probe.Calls);
    }

    // Другой кластер запрещает мутацию даже при подходящих правах SCM.
    [Fact]
    public async Task DifferentClusterIsRejectedBeforeTheAction()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Runtime, new() { CommandsEnabled = true, ExpectedSystemIdentifier = "123" });

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        var executed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Monitor.ExecuteOwnedAsync(_ => { executed = true; return Task.CompletedTask; }, CancellationToken.None));

        Assert.False(executed);
    }

    // Истечение срока остановки хоста не освобождает владение посреди мутации.
    [Fact]
    public async Task StopTimeoutAndDisposeKeepOwnershipUntilTheActionFinishes()
    {
        using var scenario = new Scenario(PostgreSqlHostKind.Runtime, new() { CommandsEnabled = true, ExpectedSystemIdentifier = "7703468038317098096" });

        scenario.Presence.Names.Add("postgresql-x64-18");

        await scenario.Monitor.RefreshAsync(CancellationToken.None);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = scenario.Monitor.ExecuteOwnedAsync(async _ => { started.SetResult(); await finish.Task; }, CancellationToken.None);

        await started.Task;

        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Monitor.StopAsync(cancelled.Token));

        scenario.Monitor.Dispose();

        Assert.Null(scenario.Leases.TryAcquire("postgresql-x64-18"));

        finish.SetResult();

        await operation;

        using var next = scenario.Leases.TryAcquire("postgresql-x64-18");

        Assert.NotNull(next);
    }

    private sealed class Presence : IPostgreSqlServicePresence
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Моделирует только наличие регистрации службы.
        public bool IsInstalled(string serviceName)
        {
            return Names.Contains(serviceName);
        }
    }

    private sealed class Probe : IPostgreSqlLocalProbe
    {
        public int Calls;

        // Моделирует чтение состояния без SQL и изменения служб.
        public Task<PostgreSqlLocalHealthResponse> CheckAsync(PostgreSqlControlOptions settings, int timeoutSeconds, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(new PostgreSqlLocalHealthResponse { PermissionsOk = true, PostgreSqlRole = "Standby", SystemIdentifier = "7703468038317098096", Service = new() { ManagedByService = true, ServiceState = "Running" } });
        }
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public Presence Presence { get; } = new();
        public Probe Probe { get; } = new();
        public PostgreSqlControllerLease Leases { get; }
        public PostgreSqlNodeMonitor Monitor { get; }

        // Использует отдельный каталог и настоящие файловые дескрипторы.
        public Scenario(PostgreSqlHostKind kind, PostgreSqlControlOptions? settings = null)
        {
            Leases = new(_root);
            Monitor = new(Options.Create(settings ?? new()), new ConfigurationBuilder().Build(), kind, Presence, Probe, Leases, NullLogger<PostgreSqlNodeMonitor>.Instance);
        }

        // Сначала освобождает хост, затем удаляет только собственные временные файлы.
        public void Dispose()
        {
            Monitor.Dispose();

            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }
}