using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlModuleDatabaseSettingsTests
{
    // Модули используют общую пару, сохраняя параметры каждого Main-профиля.
    [Theory]
    [InlineData(true, "RecipeKNG")]
    [InlineData(false, "EventPicker")]
    public async Task UsesMainProfilesAndModuleDatabase(bool recipes, string database)
    {
        var connector = new Connector { Failure = call => call == 1 ? new MainDatabaseReadOnlyException() : null };
        var factory = Factory(Configuration(), connector);

        await using var connection = await Open(factory, recipes);

        Assert.Equal(2, connector.Calls.Count);

        var first = new NpgsqlConnectionStringBuilder(connector.Calls[0]);
        var second = new NpgsqlConnectionStringBuilder(connector.Calls[1]);

        Assert.Equal(database, first.Database);
        Assert.Equal(database, second.Database);
        Assert.Equal("db-a", first.Host);
        Assert.Equal("db-b", second.Host);
        Assert.Equal(5444, second.Port);
        Assert.Equal("secondary_user", second.Username);
        Assert.Equal("test-secondary", second.Password);
        Assert.Equal(SslMode.Require, second.SslMode);
    }

    // Новое имя имеет приоритет над прежними отдельными подключениями.
    [Fact]
    public async Task DatabaseNameOverridesLegacyProfile()
    {
        var config = Configuration();
        config["Recipes:ConnectionString"] = "not a connection string";
        config["Recipes:ConnectionStringSecondary"] = "Host=wrong;Database=wrong";

        var connector = new Connector();

        await using var connection = await Factory(config, connector).OpenRecipesAsync(default);

        var settings = new NpgsqlConnectionStringBuilder(Assert.Single(connector.Calls));

        Assert.Equal("db-a", settings.Host);
        Assert.Equal("RecipeKNG", settings.Database);
        Assert.Equal("main_user", settings.Username);
    }

    // Форма переносит только имя Database из старого профиля.
    [Fact]
    public void MigrationReadsOnlyDatabaseName()
    {
        Assert.Equal("LegacyRecipes", PostgreSqlModuleDatabaseSettings.ResolveName(null, "Host=old-server;Database=LegacyRecipes;Username=old_user", "RecipeKNG"));
    }

    // Отсутствующий Main не заменяется старым сервером модуля.
    [Fact]
    public async Task MissingMainDoesNotUseLegacyServer()
    {
        var config = Configuration();
        config["Database:ConnectionString"] = "";

        var connector = new Connector();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(config, connector).OpenRecipesAsync(default));

        Assert.Empty(connector.Calls);
    }

    // Пустой Main Secondary отключает резерв и для модулей.
    [Fact]
    public async Task EmptySecondaryDisablesFallback()
    {
        var config = Configuration();
        config["Database:ConnectionStringSecondary"] = "";

        var connector = new Connector { Failure = _ => new IOException() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(config, connector).OpenRecipesAsync(default));

        Assert.Single(connector.Calls);
    }

    // Явно очищенное имя не заменяется значением по умолчанию.
    [Fact]
    public async Task EmptyNameIsRejected()
    {
        var config = Configuration();
        config["Recipes:Database"] = " ";

        var connector = new Connector();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(config, connector).OpenRecipesAsync(default));

        Assert.Empty(connector.Calls);
    }

    // Multi-host подключение выбирает writer с нужным именем БД.
    [Fact]
    public async Task MultiHostRequiresReadWrite()
    {
        var config = Configuration();
        config["Database:ConnectionString"] = "Host=db-a,db-b;Database=main;Target Session Attributes=any";
        config["Database:ConnectionStringSecondary"] = "";

        var connector = new Connector();

        await using var connection = await Factory(config, connector).OpenRecipesAsync(default);

        var settings = new NpgsqlConnectionStringBuilder(Assert.Single(connector.Calls));

        Assert.Equal("read-write", settings.TargetSessionAttributes);
        Assert.Equal(0, settings.HostRecheckSeconds);
        Assert.Equal("RecipeKNG", settings.Database);
    }

    // Сохранение удаляет старые профили и сохраняет остальные настройки.
    [Fact]
    public void SaveRemovesLegacyProfilesAndPreservesOtherSettings()
    {
        var root = JsonNode.Parse("""
        {"Recipes":{"ConnectionString":"old","ConnectionStringSecondary":"old2","CommandTimeoutSeconds":45},"EventDatabase":{"ConnectionString":"old","ConnectionStringSecondary":"old2","Provider":"PostgreSql"},"ConnectionStrings":{"EventPicker":"old","Default":"keep"},"Custom":{"Value":123}}
        """)!.AsObject();

        PostgreSqlModuleDatabaseSettings.Apply(root, "RecipeKNG", "EventPicker");

        Assert.Equal("RecipeKNG", root["Recipes"]!["Database"]!.GetValue<string>());
        Assert.Equal("EventPicker", root["EventDatabase"]!["Database"]!.GetValue<string>());
        Assert.Equal(45, root["Recipes"]!["CommandTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal("PostgreSql", root["EventDatabase"]!["Provider"]!.GetValue<string>());
        Assert.Equal("keep", root["ConnectionStrings"]!["Default"]!.GetValue<string>());
        Assert.Equal(123, root["Custom"]!["Value"]!.GetValue<int>());

        foreach (var section in new[] { "Recipes", "EventDatabase" })
        {
            Assert.Null(root[section]!["ConnectionString"]);
            Assert.Null(root[section]!["ConnectionStringSecondary"]);
        }

        Assert.Null(root["ConnectionStrings"]!["EventPicker"]);
    }

    // Неполный ввод отклоняется до изменения JSON.
    [Theory]
    [InlineData("", "EventPicker")]
    [InlineData("RecipeKNG", "")]
    public void InvalidNamesDoNotModifyJson(string recipes, string events)
    {
        var root = JsonNode.Parse("{\"Recipes\":{\"ConnectionString\":\"keep\"}}")!.AsObject();
        var before = root.ToJsonString();

        Assert.Throws<InvalidOperationException>(() => PostgreSqlModuleDatabaseSettings.Apply(root, recipes, events));

        Assert.Equal(before, root.ToJsonString());
    }

    // Символы имени не превращаются в дополнительные параметры подключения.
    [Fact]
    public void DatabaseNameCannotInjectConnectionSettings()
    {
        var value = PostgreSqlModuleDatabaseSettings.ConnectionString("Host=db-a;Database=main;Username=main_user", "name;Host=other");
        var settings = new NpgsqlConnectionStringBuilder(value);

        Assert.Equal("db-a", settings.Host);
        Assert.Equal("name;Host=other", settings.Database);
    }

    // Создаёт общую тестовую пару.
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Database:ConnectionString"] = "Host=db-a;Database=main;Username=main_user;Password=test-primary",
        ["Database:ConnectionStringSecondary"] = "Host=db-b;Port=5444;Database=main;Username=secondary_user;Password=test-secondary;SSL Mode=Require",
        ["Recipes:Database"] = "RecipeKNG",
        ["EventDatabase:Database"] = "EventPicker"
    }).Build();

    // Выбирает проверяемый модуль.
    private static Task<NpgsqlConnection> Open(PostgreSqlModuleConnections factory, bool recipes) => recipes ? factory.OpenRecipesAsync(default) : factory.OpenEventLogAsync(default);

    // Создаёт фабрику с настоящим общим guard.
    private static PostgreSqlModuleConnections Factory(IConfiguration config, Connector connector) => new(config, connector, new MainDatabaseWriteGuard(config, NullLogger<MainDatabaseWriteGuard>.Instance), NullLogger<PostgreSqlModuleConnections>.Instance);

    // Подменяет только открытие соединения.
    private sealed class Connector : IMainDatabaseConnector
    {
        public List<string> Calls { get; } = [];
        public Func<int, Exception?>? Failure { get; init; }

        public Task<NpgsqlConnection> OpenAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add(connectionString);

            if (Failure?.Invoke(Calls.Count) is { } error)
                return Task.FromException<NpgsqlConnection>(error);

            return Task.FromResult(new NpgsqlConnection());
        }
    }
}