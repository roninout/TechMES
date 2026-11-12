using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TechMES.Application.EventLog;
using TechMES.Application.Recipes;
using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.PostgreSql;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlModuleConnectionsTests
{
    // Peer меняется, Database и учётные данные остаются от модуля.
    [Theory]
    [InlineData("db-a", 5432, "db-b", 5433)]
    [InlineData("db-b", 5433, "db-a", 5432)]
    public async Task DerivesPeerAndPreservesModuleSettings(string host, int port, string peer, int peerPort)
    {
        var config = Configuration($"Host={host};Port={port};Database=recipes;Username=recipe_user;Password=test-secret;SSL Mode=Require");
        var connector = new Connector { Failure = call => call == 1 ? new MainDatabaseReadOnlyException() : null };

        await using var connection = await Factory(config, connector).OpenRecipesAsync(default);

        Assert.Equal(2, connector.Calls.Count);

        var second = new NpgsqlConnectionStringBuilder(connector.Calls[1]);

        Assert.Equal(peer, second.Host);
        Assert.Equal(peerPort, second.Port);
        Assert.Equal("recipes", second.Database);
        Assert.Equal("recipe_user", second.Username);
        Assert.Equal("test-secret", second.Password);
        Assert.Equal(SslMode.Require, second.SslMode);
    }

    // Для отдельного сервера не угадываем резерв из другого кластера.
    [Fact]
    public async Task IndependentDatabaseHasNoGuessedPeer()
    {
        var connector = new Connector { Failure = _ => new IOException() };
        var factory = Factory(Configuration("Host=external;Database=recipes"), connector);

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenRecipesAsync(default));

        Assert.Single(connector.Calls);
    }

    // Явный резерв сохраняет собственные порт и учётную запись.
    [Fact]
    public async Task ExplicitSecondaryTakesPriority()
    {
        var config = Configuration("Host=external-a;Database=recipes;Username=one", "Host=external-b;Port=5444;Database=recipes;Username=two");
        var connector = new Connector { Failure = call => call == 1 ? new IOException() : null };

        await using var connection = await Factory(config, connector).OpenRecipesAsync(default);

        var second = new NpgsqlConnectionStringBuilder(connector.Calls[1]);

        Assert.Equal("external-b", second.Host);
        Assert.Equal(5444, second.Port);
        Assert.Equal("two", second.Username);
    }

    // Разные имена БД отклоняются до первого подключения.
    [Fact]
    public async Task RejectsDifferentDatabaseNames()
    {
        var connector = new Connector();
        var factory = Factory(Configuration("Host=db-a;Database=recipes", "Host=db-b;Database=events"), connector);

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenRecipesAsync(default));

        Assert.Empty(connector.Calls);
    }

    // EventPicker сохраняет собственную БД.
    [Fact]
    public async Task EventPickerKeepsItsOwnDatabase()
    {
        var connector = new Connector();

        await using var connection = await Factory(Configuration("Host=db-a;Database=recipes"), connector).OpenEventLogAsync(default);

        Assert.Equal("events", new NpgsqlConnectionStringBuilder(Assert.Single(connector.Calls)).Database);
    }

    // Read-only соединения не выдаются хранилищу.
    [Fact]
    public async Task RejectsPairWithoutWriter()
    {
        var connector = new Connector { Failure = _ => new MainDatabaseReadOnlyException() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(Configuration("Host=db-a;Database=recipes"), connector).OpenRecipesAsync(default));

        Assert.Equal(2, connector.Calls.Count);
    }

    // Отмена запроса не приводит к попытке на другом сервере.
    [Fact]
    public async Task CancellationDoesNotFallBack()
    {
        using var stop = new CancellationTokenSource();

        var connector = new Connector { BeforeReturn = () => stop.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Factory(Configuration("Host=db-a;Database=recipes"), connector).OpenRecipesAsync(stop.Token));

        Assert.Single(connector.Calls);
    }

    // Общий запрет срабатывает до открытия соединения.
    [Fact]
    public async Task GuardBlocksBeforeOpen()
    {
        var config = Configuration("Host=db-a;Database=recipes");
        var guard = Guard(config);

        Block(guard);

        var connector = new Connector();
        var factory = new PostgreSqlModuleConnections(config, connector, guard, NullLogger<PostgreSqlModuleConnections>.Instance);

        await Assert.ThrowsAsync<MainDatabaseWriteBlockedException>(() => factory.OpenRecipesAsync(default));

        Assert.Empty(connector.Calls);
    }

    // Запрет во время открытия также не обходится через резерв.
    [Fact]
    public async Task GuardDuringOpenDoesNotFallBack()
    {
        var config = Configuration("Host=db-a;Database=recipes");
        var guard = Guard(config);
        var connector = new Connector { BeforeReturn = () => Block(guard) };
        var factory = new PostgreSqlModuleConnections(config, connector, guard, NullLogger<PostgreSqlModuleConnections>.Instance);

        await Assert.ThrowsAsync<MainDatabaseWriteBlockedException>(() => factory.OpenRecipesAsync(default));

        Assert.Single(connector.Calls);
    }

    // Multi-host профиль требует read-write сервер.
    [Fact]
    public async Task MultiHostRequiresReadWrite()
    {
        var connector = new Connector();

        await using var connection = await Factory(Configuration("Host=db-a,db-b;Database=recipes;Target Session Attributes=any"), connector).OpenRecipesAsync(default);

        var settings = new NpgsqlConnectionStringBuilder(Assert.Single(connector.Calls));

        Assert.Equal("read-write", settings.TargetSessionAttributes);
        Assert.Equal(0, settings.HostRecheckSeconds);
    }

    // Каждый модуль самостоятельно регистрирует необходимые зависимости.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistrationResolvesWithoutOtherModules(bool recipes)
    {
        var config = Configuration("Host=db-a;Database=recipes");
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config);

        if (recipes)
            services.AddRecipeInfrastructure();
        else
            services.AddPostgreSqlEventLogInfrastructure(config);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        if (recipes)
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRecipeStore>());
        else
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEventLogStore>());
    }

    // Создаёт тестовые настройки разных БД.
    private static IConfiguration Configuration(string recipes, string? secondary = null)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Host=db-a;Port=5432;Database=main;Username=main_user",
            ["Database:ConnectionStringSecondary"] = "Host=db-b;Port=5433;Database=main;Username=main_user",
            ["Recipes:ConnectionString"] = recipes,
            ["Recipes:ConnectionStringSecondary"] = secondary,
            ["EventDatabase:ConnectionString"] = "Host=db-a;Database=events;Username=event_user"
        }).Build();
    }

    // Создаёт механизм подключения с настоящим guard.
    private static PostgreSqlModuleConnections Factory(IConfiguration config, Connector connector) => new(config, connector, Guard(config), NullLogger<PostgreSqlModuleConnections>.Instance);

    // Создаёт общий guard.
    private static MainDatabaseWriteGuard Guard(IConfiguration config) => new(config, NullLogger<MainDatabaseWriteGuard>.Instance);

    // Передаёт guard свежие сведения о двух Primary.
    private static void Block(MainDatabaseWriteGuard guard)
    {
        var at = DateTimeOffset.UtcNow;

        PostgreSqlServerState Node(string address) => new()
        {
            Configured = true,
            Connected = true,
            CheckedAtUtc = at,
            SystemIdentifier = "123",
            ActualRole = "Primary",
            ServerAddress = address,
            ServerPort = 5432
        };

        guard.Observe(new() { Enabled = true, HealthPeriodSeconds = 10, Primary = Node("db-a"), Secondary = Node("db-b") }, at);
    }

    // Подменяет только открытие соединения.
    private sealed class Connector : IMainDatabaseConnector
    {
        public List<string> Calls { get; } = [];
        public Func<int, Exception?>? Failure { get; init; }
        public Action? BeforeReturn { get; init; }

        public Task<NpgsqlConnection> OpenAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add(connectionString);

            if (Failure?.Invoke(Calls.Count) is { } error)
                return Task.FromException<NpgsqlConnection>(error);

            BeforeReturn?.Invoke();

            return Task.FromResult(new NpgsqlConnection());
        }
    }
}