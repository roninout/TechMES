using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TechMES.Infrastructure.PostgreSql.Connections;

// Открывает БД модулей на доступном writer, сохраняя их Database и учётные данные.
public sealed class PostgreSqlModuleConnections(IConfiguration configuration, IMainDatabaseConnector connector, MainDatabaseWriteGuard writeGuard, ILogger<PostgreSqlModuleConnections> logger)
{
    // Рецепты используют собственную БД и собственный профиль подключения.
    public Task<NpgsqlConnection> OpenRecipesAsync(CancellationToken ct) => OpenAsync("Recipes", configuration["Recipes:ConnectionString"], configuration["Recipes:ConnectionStringSecondary"], ct);

    // WEB читает EventPicker с writer.
    public Task<NpgsqlConnection> OpenEventLogAsync(CancellationToken ct) => OpenAsync("EventPicker", First(configuration.GetConnectionString("EventPicker"), configuration["EventDatabase:ConnectionString"]), configuration["EventDatabase:ConnectionStringSecondary"], ct);

    // Повторяет только открытие и проверку соединения, до исполнения SQL хранилища.
    private async Task<NpgsqlConnection> OpenAsync(string module, string? primary, string? secondary, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        writeGuard.ThrowIfBlocked();

        var candidates = Candidates(module, primary, secondary);
        var seconds = Math.Clamp(configuration.GetValue("CtApi:HealthCheckPeriodSeconds", 10), 5, 300);

        Exception? failure = null;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            writeGuard.ThrowIfBlocked();

            try
            {
                var connection = await connector.OpenAsync(candidate, seconds, ct);

                try
                {
                    ct.ThrowIfCancellationRequested();
                    writeGuard.ThrowIfBlocked();

                    return connection;
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is NpgsqlException or TimeoutException or IOException or MainDatabaseReadOnlyException))
            {
                failure = ex;

                // Доступная Standby является нормальным состоянием резервирования.
                if (ex is not MainDatabaseReadOnlyException)
                    logger.LogWarning("{Module} database open failed. ErrorType={ErrorType}.", module, ex.GetType().Name);
            }
        }

        throw new InvalidOperationException($"No configured {module} database is available for writing.", failure);
    }

    // Явная настройка резерва имеет приоритет перед определением peer из Main DB.
    private string[] Candidates(string module, string? primary, string? secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
            throw new InvalidOperationException($"{module} database connection is not configured.");

        var first = new NpgsqlConnectionStringBuilder(primary);

        if (string.IsNullOrWhiteSpace(first.Host))
            throw new InvalidOperationException($"{module} database host is not configured.");

        NpgsqlConnectionStringBuilder? second = null;

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            second = new NpgsqlConnectionStringBuilder(secondary);

            if (string.IsNullOrWhiteSpace(second.Host))
                throw new InvalidOperationException($"{module} Secondary host is not configured.");

            var firstDatabase = First(first.Database, first.Username);
            var secondDatabase = First(second.Database, second.Username);

            if (!string.Equals(firstDatabase, secondDatabase, StringComparison.Ordinal))
                throw new InvalidOperationException($"{module} Primary and Secondary must select the same database name.");
        }
        else
        {
            var mainPrimary = First(configuration.GetConnectionString("Default"), configuration["Database:ConnectionString"]);
            var mainSecondary = configuration["Database:ConnectionStringSecondary"];

            if (!string.IsNullOrWhiteSpace(mainPrimary) && !string.IsNullOrWhiteSpace(mainSecondary))
            {
                var a = new NpgsqlConnectionStringBuilder(mainPrimary);
                var b = new NpgsqlConnectionStringBuilder(mainSecondary);

                var peer = SameEndpoint(first, a)
                    ? b
                    : SameEndpoint(first, b)
                        ? a
                        : null;

                if (peer?.Host is { Length: > 0 } peerHost && !peerHost.Contains(','))
                {
                    second = new NpgsqlConnectionStringBuilder(first.ConnectionString)
                    {
                        Host = peerHost,
                        Port = peer.Port
                    };
                }
            }
        }

        // Для уже заданного списка хостов драйвер выбирает только read-write узел.
        foreach (var item in new[] { first, second })
        {
            if (item?.Host?.Contains(',') == true)
            {
                item.TargetSessionAttributes = "read-write";
                item.HostRecheckSeconds = 0;
            }
        }

        return second is null || first.ConnectionString == second.ConnectionString
            ? [first.ConnectionString]
            : [first.ConnectionString, second.ConnectionString];
    }

    // Сравнивает одиночные endpoint; не угадывает соответствие DNS-имени и IP.
    private static bool SameEndpoint(NpgsqlConnectionStringBuilder a, NpgsqlConnectionStringBuilder b) => a.Host is { Length: > 0 } ah && b.Host is { Length: > 0 } bh && !ah.Contains(',') && !bh.Contains(',') && a.Port == b.Port && string.Equals(ah.Trim(), bh.Trim(), StringComparison.OrdinalIgnoreCase);

    // Пустой устаревший alias не перекрывает заполненную штатную настройку.
    private static string? First(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a : b;
}