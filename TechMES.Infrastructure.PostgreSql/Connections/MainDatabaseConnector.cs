using Npgsql;

namespace TechMES.Infrastructure.PostgreSql.Connections;

public interface IMainDatabaseConnector
{
    Task<NpgsqlConnection> OpenAsync(string connectionString, int timeoutSeconds, CancellationToken ct);
}

/// <summary>
/// Проверяет реальную связь и режим записи до передачи соединения store-у.
/// </summary>
public sealed class MainDatabaseConnector : IMainDatabaseConnector
{
    public async Task<NpgsqlConnection> OpenAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = timeoutSeconds };
        var connection = new NpgsqlConnection(settings.ConnectionString);

        try
        {
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand("SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'off';", connection) { CommandTimeout = timeoutSeconds };

            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new MainDatabaseReadOnlyException();

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Сервер доступен, но не подходит для общего Main DB, который выполняет записи.
/// </summary>
public sealed class MainDatabaseReadOnlyException : InvalidOperationException
{
    public MainDatabaseReadOnlyException() : base("Main database is read-only.") { }
}