using Npgsql;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Проверяет PostgreSQL уже на уровне авторизации, а не только открытого TCP-порта.
/// Runtime остается основным владельцем PostgreSQL-логики, но Maintenance может быстро подтвердить,
/// что connection string принимает логин и отдает версию сервера.
/// </summary>
public sealed class PostgreSqlProbeService
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Открывает PostgreSQL-подключение и читает версию сервера, текущую БД и пользователя.
    /// </summary>
    public async Task<PostgreSqlProbeResult> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = (int)OpenTimeout.TotalSeconds,
                CommandTimeout = (int)OpenTimeout.TotalSeconds
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(
                "select version(), current_database(), current_user;",
                connection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return PostgreSqlProbeResult.Fail("PostgreSQL did not return version row.");

            return PostgreSqlProbeResult.Ok(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2));
        }
        catch (Exception ex)
        {
            return PostgreSqlProbeResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Результат PostgreSQL-проверки.
/// </summary>
public sealed class PostgreSqlProbeResult
{
    /// <summary>
    /// True, если подключение и запрос версии прошли успешно.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Полная строка version() от PostgreSQL.
    /// </summary>
    public string Version { get; init; } = "";

    /// <summary>
    /// Имя текущей БД.
    /// </summary>
    public string Database { get; init; } = "";

    /// <summary>
    /// Пользователь, под которым выполнена авторизация.
    /// </summary>
    public string User { get; init; } = "";

    /// <summary>
    /// Текст ошибки, если проверка не прошла.
    /// </summary>
    public string Error { get; init; } = "";

    /// <summary>
    /// Успешный результат.
    /// </summary>
    public static PostgreSqlProbeResult Ok(
        string version,
        string database,
        string user) =>
        new()
        {
            Success = true,
            Version = version,
            Database = database,
            User = user
        };

    /// <summary>
    /// Ошибка проверки.
    /// </summary>
    public static PostgreSqlProbeResult Fail(string error) =>
        new()
        {
            Success = false,
            Error = error
        };
}
