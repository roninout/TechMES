using Microsoft.Extensions.Configuration;
using Npgsql;
using TechMES.Contracts.Runtime;

namespace TechMES.Infrastructure.PostgreSql.Connections;

public interface IPostgreSqlClusterProbe
{
    /// <summary>
    /// Читает роль и состояние репликации одного сервера с общим ограничением времени.
    /// </summary>
    Task<PostgreSqlServerState> ProbeAsync(string connectionString, int timeoutSeconds, CancellationToken ct);
}

/// <summary>
/// Прямые подключения к двум серверам, включая read-only реплику.
/// Никаких изменений БД, повышения реплики и управления службой.
/// </summary>
public sealed class PostgreSqlClusterProbe : IPostgreSqlClusterProbe
{
    /// <summary>
    /// Проверяет соединение, затем отдельно читает диагностические данные. Недостаток прав на диагностику не превращает доступную БД в Offline.
    /// </summary>
    public async Task<PostgreSqlServerState> ProbeAsync(string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        var state = new PostgreSqlServerState();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Multiplexing = false,
                Timeout = timeoutSeconds,
                CommandTimeout = timeoutSeconds,
                CancellationTimeout = 1000,
                TargetSessionAttributes = "any",
                ApplicationName = "TechMES.ClusterMonitor"
            };

            // Каждое поле должно проверять один конкретный сервер.
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Contains(','))
                return state with { Message = "Configure exactly one PostgreSQL host in each connection string." };

            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(timeout.Token);

            const string roleSql = """
                SELECT host(inet_server_addr()), inet_server_port(), pg_is_in_recovery(),
                       current_setting('transaction_read_only')::boolean;
                """;

            await using (var command = new NpgsqlCommand(roleSql, connection))
            await using (var reader = await command.ExecuteReaderAsync(timeout.Token))
            {
                await reader.ReadAsync(timeout.Token);

                var standby = reader.GetBoolean(2);

                state = state with
                {
                    Connected = true,
                    ServerAddress = Text(reader, 0),
                    ServerPort = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    ActualRole = standby ? "Standby" : "Primary",
                    CanWrite = !standby && !reader.GetBoolean(3)
                };
            }

            const string identitySql = """
                SELECT system_identifier::text, pg_has_role(current_user, 'pg_read_all_stats', 'USAGE')
                FROM pg_catalog.pg_control_system();
                """;

            await using (var command = new NpgsqlCommand(identitySql, connection))
            await using (var reader = await command.ExecuteReaderAsync(timeout.Token))
            {
                await reader.ReadAsync(timeout.Token);

                state = state with { SystemIdentifier = reader.GetString(0) };

                if (!reader.GetBoolean(1))
                    return state with { Message = "Connected; replication statistics require pg_read_all_stats." };
            }

            if (state.ActualRole == "Standby")
            {
                const string sql = """
                    SELECT pg_last_wal_receive_lsn()::text, pg_last_wal_replay_lsn()::text,
                           pg_is_wal_replay_paused(), status, sender_host, slot_name, last_msg_receipt_time
                    FROM (SELECT 1) AS one LEFT JOIN pg_catalog.pg_stat_wal_receiver ON true;
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync(timeout.Token);

                await reader.ReadAsync(timeout.Token);

                state = state with
                {
                    ReceivedWalLsn = Text(reader, 0),
                    ReplayedWalLsn = Text(reader, 1),
                    ReplayPaused = reader.GetBoolean(2),
                    ReceiverStatus = Text(reader, 3),
                    SenderHost = Text(reader, 4),
                    SlotName = Text(reader, 5),
                    LastMessageReceivedAtUtc = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)
                };
            }
            else
            {
                const string sql = """
                    SELECT pg_current_wal_flush_lsn()::text,
                           (SELECT count(*)::integer FROM pg_catalog.pg_stat_replication WHERE state = 'streaming');
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync(timeout.Token);

                await reader.ReadAsync(timeout.Token);

                state = state with
                {
                    CurrentWalLsn = Text(reader, 0),
                    StreamingReplicaCount = reader.GetInt32(1)
                };
            }

            return state with
            {
                MonitoringOk = true,
                Message = "PostgreSQL role and replication diagnostics read successfully."
            };
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();

            // Текст исключения и строка подключения в диагностическое API не попадают.
            var error = ex is PostgresException postgres ? postgres.SqlState : ex.GetType().Name;

            return state with
            {
                Message = state.Connected
                    ? $"Connected; monitoring unavailable ({error})."
                    : $"Connection check failed ({error})."
            };
        }
    }

    /// <summary>
    /// Читает необязательное текстовое поле PostgreSQL, сохраняя SQL NULL как null.
    /// </summary>
    private static string? Text(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
}

/// <summary>
/// Независим от активности Runtime и writable-маршрута MainDatabaseFailover.
/// Endpoint читает только опубликованный снимок и не запускает SQL.
/// </summary>
public sealed class PostgreSqlClusterMonitor
{
    private readonly IPostgreSqlClusterProbe _probe;
    private readonly MainDatabaseWriteGuard _writeGuard;
    private readonly string[] _connections;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private PostgreSqlClusterHealthResponse _state;

    /// <summary>
    /// Запоминает существующие подключения Primary/Secondary и общий Health Period. Соединения в конструкторе не открываются.
    /// </summary>
    public PostgreSqlClusterMonitor(IConfiguration configuration, IPostgreSqlClusterProbe probe, MainDatabaseWriteGuard writeGuard)
    {
        _probe = probe;
        _writeGuard = writeGuard;
        _connections = [(configuration.GetConnectionString("Default") ?? configuration["Database:ConnectionString"])?.Trim() ?? "", configuration["Database:ConnectionStringSecondary"]?.Trim() ?? ""];

        _state = new()
        {
            Enabled = string.Equals(configuration["Database:Provider"] ?? "PostgreSql", "PostgreSql", StringComparison.OrdinalIgnoreCase),
            HealthPeriodSeconds = Math.Max(5, configuration.GetValue<int?>("CtApi:HealthCheckPeriodSeconds") ?? 10),
            Primary = new() { Role = "Primary", Configured = _connections[0].Length > 0 },
            Secondary = new() { Role = "Secondary", Configured = _connections[1].Length > 0 }
        };
    }

    /// <summary>
    /// Возвращает готовый снимок без SQL. Просроченные сведения помечает Stale.
    /// </summary>
    public PostgreSqlClusterHealthResponse GetHealth()
    {
        var state = Volatile.Read(ref _state);
        var now = DateTimeOffset.UtcNow;

        var stale = new[] { state.Primary, state.Secondary }.Any(server => server.Configured && server.CheckedAtUtc is not null && now - server.CheckedAtUtc.Value > TimeSpan.FromSeconds(state.HealthPeriodSeconds * 3d));

        return stale ? state with { Topology = "Stale" } : state;
    }

    /// <summary>
    /// Параллельно проверяет оба сервера и атомарно публикует общий результат. Повторный цикл пропускается, пока предыдущий ещё выполняется.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct))
            return;

        try
        {
            var previous = Volatile.Read(ref _state);

            if (!previous.Enabled)
                return;

            var results = await Task.WhenAll(
                CheckAsync(0, previous.Primary, previous.HealthPeriodSeconds, ct),
                CheckAsync(1, previous.Secondary, previous.HealthPeriodSeconds, ct));

            ct.ThrowIfCancellationRequested();

            var next = previous with
            {
                Primary = results[0],
                Secondary = results[1]
            };

            next = next with { Topology = Describe(next.Primary, next.Secondary) };
            _writeGuard.Observe(next);
            Volatile.Write(ref _state, next);
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>
    /// Проверяет настроенный сервер и обновляет его независимые счётчики соединения. Пустое резервное подключение не опрашивается.
    /// </summary>
    private async Task<PostgreSqlServerState> CheckAsync(int index, PostgreSqlServerState previous, int period, CancellationToken ct)
    {
        if (!previous.Configured)
            return previous with { Message = "Not configured." };

        var result = await _probe.ProbeAsync(_connections[index], Math.Clamp(period / 2, 1, 5), ct);

        return result with
        {
            Role = previous.Role,
            Configured = true,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            ConsecutiveSuccesses = result.Connected ? Math.Min(previous.ConsecutiveSuccesses, 999999) + 1 : 0,
            ConsecutiveFailures = result.Connected ? 0 : Math.Min(previous.ConsecutiveFailures, 999999) + 1
        };
    }

    /// <summary>
    /// Описывает наблюдаемую топологию и обнаруженные противоречия. Результат не является разрешением на повышение реплики.
    /// </summary>
    private static string Describe(PostgreSqlServerState primary, PostgreSqlServerState secondary)
    {
        if (!primary.Configured || !secondary.Configured) return "SingleServer";
        if (!primary.Connected || !secondary.Connected) return "Degraded";

        if (primary.ServerAddress is not null && primary.ServerAddress == secondary.ServerAddress && primary.ServerPort == secondary.ServerPort)
            return "DuplicateEndpoint";

        if (primary.SystemIdentifier is not null && secondary.SystemIdentifier is not null && primary.SystemIdentifier != secondary.SystemIdentifier)
            return "ClusterMismatch";

        if (primary.ActualRole == "Primary" && secondary.ActualRole == "Primary") return "TwoPrimaries";
        if (primary.ActualRole == "Standby" && secondary.ActualRole == "Standby") return "TwoStandbys";
        if (!primary.MonitoringOk || !secondary.MonitoringOk) return "MonitoringIncomplete";

        var writer = primary.ActualRole == "Primary" ? primary : secondary;
        var standby = primary.ActualRole == "Standby" ? primary : secondary;

        if (!writer.CanWrite || standby.ReceiverStatus != "streaming" || standby.ReplayPaused != false || writer.StreamingReplicaCount is not > 0)
            return "ReplicationUnhealthy";

        return writer.Role == "Primary" ? "PreferredPrimary" : "PreferredSecondary";
    }
}
