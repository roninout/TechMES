using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.PostgreSql.Connections;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;

namespace TechMES.PostgreSql.Tests;

public sealed class MainDatabaseWriteGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    // Здоровый резервный writer после переключения не должен блокироваться из-за имени поля Secondary.
    [Fact]
    public void HealthyPairInEitherDirectionIsAllowed()
    {
        foreach (var reverse in new[] { false, true })
        {
            var guard = CreateGuard();
            guard.Observe(Pair(Now, reverse), Now);
            Assert.False(guard.GetStatus().WritesBlocked);
        }
    }

    // Каждый из трёх конфликтов запрещает выдачу любого Main DB соединения.
    [Theory]
    [InlineData("TwoPrimaries")]
    [InlineData("ClusterMismatch")]
    [InlineData("DuplicateEndpoint")]
    public void ConflictBlocksBothRoutes(string code)
    {
        var guard = CreateGuard();
        guard.Observe(Conflict(Now, code), Now);
        Assert.Equal(code, guard.GetStatus().Code);
        Assert.Throws<MainDatabaseWriteBlockedException>(() => guard.ThrowIfBlocked());
    }

    // Offline, Stale, отсутствие статистики и два Standby не доказывают исправление конфликта.
    [Theory]
    [InlineData("Offline")]
    [InlineData("Stale")]
    [InlineData("Unknown")]
    [InlineData("TwoStandbys")]
    [InlineData("WrongSender")]
    public void IncompleteRecoveryKeepsBlock(string failure)
    {
        var guard = CreateGuard();
        guard.Observe(Conflict(Now, "TwoPrimaries"), Now);
        var at = Now.AddSeconds(10);
        var healthy = Pair(at);
        var bad = failure switch
        {
            "Offline" => healthy with { Secondary = healthy.Secondary with { Connected = false } },
            "Stale" => Pair(Now.AddMinutes(-5)),
            "Unknown" => healthy with { Secondary = healthy.Secondary with { MonitoringOk = false } },
            "WrongSender" => healthy with { Secondary = healthy.Secondary with { SenderHost = "192.168.1.200" } },
            _ => healthy with { Primary = healthy.Primary with { ActualRole = "Standby", CanWrite = false } }
        };
        for (var i = 0; i < 4; i++)
            guard.Observe(bad, at);
        Assert.True(guard.GetStatus().WritesBlocked);
        Assert.Equal(0, guard.GetStatus().RecoveryChecks);
    }

    // Повторный HTTP-запрос и повторная передача того же снимка не увеличивают счётчик восстановления.
    [Fact]
    public void RecoveryRequiresIndependentHealthySamples()
    {
        var guard = CreateGuard();
        guard.Observe(Conflict(Now, "TwoPrimaries"), Now);
        var one = Pair(Now.AddSeconds(10), true);
        for (var i = 0; i < 10; i++)
        {
            guard.Observe(one, Now.AddSeconds(10));
            _ = guard.GetStatus();
        }
        Assert.Equal(1, guard.GetStatus().RecoveryChecks);
        Assert.True(guard.GetStatus().WritesBlocked);
        guard.Observe(Pair(Now.AddSeconds(20), true), Now.AddSeconds(20));
        Assert.True(guard.GetStatus().WritesBlocked);
        guard.Observe(Pair(Now.AddSeconds(30), true), Now.AddSeconds(30));
        Assert.False(guard.GetStatus().WritesBlocked);
        Assert.Equal("Ready", guard.GetStatus().Code);
    }

    // Неуспешная проверка между успешными обнуляет последовательность восстановления.
    [Fact]
    public void RecoveryIsConsecutive()
    {
        var guard = CreateGuard();
        guard.Observe(Conflict(Now, "TwoPrimaries"), Now);
        guard.Observe(Pair(Now.AddSeconds(10)), Now.AddSeconds(10));
        var bad = Pair(Now.AddSeconds(20));
        guard.Observe(bad with { Secondary = bad.Secondary with { Connected = false } }, Now.AddSeconds(20));
        guard.Observe(Pair(Now.AddSeconds(30)), Now.AddSeconds(30));
        Assert.True(guard.GetStatus().WritesBlocked);
        Assert.Equal(1, guard.GetStatus().RecoveryChecks);
    }

    // Без Secondary поведение существующей одиночной БД сохраняется.
    [Fact]
    public void SingleServerDoesNotEnableGuard()
    {
        var guard = CreateGuard(false);
        guard.Observe(Conflict(Now, "TwoPrimaries"), Now);
        Assert.False(guard.GetStatus().Enabled);
        Assert.False(guard.GetStatus().WritesBlocked);
    }

    // Уже установленный запрет срабатывает до подключения и не превращает сервер в Offline.
    [Fact]
    public async Task BlockedOpenDoesNotTryAnotherServer()
    {
        var guard = CreateGuard();
        guard.Observe(Conflict(Now, "TwoPrimaries"), Now);
        var connector = new FakeConnector();
        await using var database = new MainDatabaseFailover(Configuration(), connector, NullLogger<MainDatabaseFailover>.Instance, guard);
        await Assert.ThrowsAsync<MainDatabaseWriteBlockedException>(() => database.OpenAsync());
        Assert.Equal(0, connector.Calls);
        Assert.Null(database.GetHealth().ActiveRole);
        Assert.True(database.GetHealth().WriteSafety.WritesBlocked);
    }

    // Конфликт, замеченный во время открытия, закрывает ещё не выданное соединение и не запускает fallback.
    [Fact]
    public async Task ConflictDuringOpenDoesNotEscapeThroughFallback()
    {
        var guard = CreateGuard();
        var connectionOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeConnector { DuringOpen = count => { if (count >= 3) { guard.Observe(Conflict(Now, "TwoPrimaries"), Now); connectionOpened.TrySetResult(); } } };
        await using var database = new MainDatabaseFailover(Configuration(), connector, NullLogger<MainDatabaseFailover>.Instance, guard);
        database.RefreshHealth();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!database.GetHealth().Primary.Connected || !database.GetHealth().Secondary.Connected)
            await Task.Delay(10, timeout.Token);
        await Assert.ThrowsAsync<MainDatabaseWriteBlockedException>(() => database.OpenAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await connectionOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, connector.Calls);
        Assert.True(database.GetHealth().Primary.Connected);
        Assert.True(database.GetHealth().Secondary.Connected);
    }

    // HTTP возвращает 503 рабочему маршруту, но оставляет диагностику доступной в том же состоянии.
    [Theory]
    [InlineData(false, 503)]
    [InlineData(true, 204)]
    public async Task MiddlewareKeepsDiagnosticsAccessible(bool control, int expectedStatus)
    {
        var guard = CreateGuard();
        guard.Observe(Conflict(Now, "TwoPrimaries"), Now);
        using var activity = new RuntimeActivityState(Options.Create(new RuntimeOptions()), NullLogger<RuntimeActivityState>.Instance);
        var context = new DefaultHttpContext();
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(control ? new object[] { new RuntimeControlEndpoint() } : Array.Empty<object>()), "test"));
        var middleware = new RuntimeActivityMiddleware(ctx => { ctx.Response.StatusCode = 204; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, activity, guard);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        if (!control)
        {
            context.Response.Body.Position = 0;
            Assert.Contains("database.write-blocked", await new StreamReader(context.Response.Body).ReadToEndAsync());
        }
    }

    // Создаёт настройки без реальных адресов подключения и паролей.
    private static IConfiguration Configuration(bool secondary = true) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = "Host=primary;Database=test", ["Database:ConnectionStringSecondary"] = secondary ? "Host=secondary;Database=test" : "", ["Runtime:Redundancy:RecoveryThreshold"] = "3" }).Build();

    // Создаёт независимый экземпляр защиты для каждого сценария.
    private static MainDatabaseWriteGuard CreateGuard(bool secondary = true) => new(Configuration(secondary), NullLogger<MainDatabaseWriteGuard>.Instance);

    // Формирует согласованную пару в обоих направлениях репликации.
    private static PostgreSqlClusterHealthResponse Pair(DateTimeOffset at, bool reverse = false)
    {
        var writerAddress = reverse ? "192.168.1.131" : "192.168.1.132";
        var writer = new PostgreSqlServerState { Configured = true, Connected = true, CanWrite = true, ActualRole = "Primary", SystemIdentifier = "7703468038317098096", ServerAddress = writerAddress, ServerPort = 5432, MonitoringOk = true, StreamingReplicaCount = 1, CheckedAtUtc = at };
        var standby = writer with { CanWrite = false, ActualRole = "Standby", ServerAddress = reverse ? "192.168.1.132" : "192.168.1.131", StreamingReplicaCount = null, ReceiverStatus = "streaming", ReplayPaused = false, SenderHost = writerAddress };
        return new() { Enabled = true, HealthPeriodSeconds = 10, Primary = reverse ? standby : writer, Secondary = reverse ? writer : standby };
    }

    // Меняет одну причину конфликта, сохраняя остальные входные данные здоровыми.
    private static PostgreSqlClusterHealthResponse Conflict(DateTimeOffset at, string code)
    {
        var pair = Pair(at);
        return pair with { Secondary = code switch { "ClusterMismatch" => pair.Secondary with { SystemIdentifier = "999" }, "DuplicateEndpoint" => pair.Secondary with { ServerAddress = pair.Primary.ServerAddress }, _ => pair.Secondary with { ActualRole = "Primary", CanWrite = true } } };
    }

    private sealed class FakeConnector : IMainDatabaseConnector
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Action<int>? DuringOpen { get; init; }

        // Возвращает отдельный объект соединения без сети; сценарий управляет моментом обнаружения конфликта.
        public Task<NpgsqlConnection> OpenAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
        {
            DuringOpen?.Invoke(Interlocked.Increment(ref _calls));
            return Task.FromResult(new NpgsqlConnection());
        }
    }
}
