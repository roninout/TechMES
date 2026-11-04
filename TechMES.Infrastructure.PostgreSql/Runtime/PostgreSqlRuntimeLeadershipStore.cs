using Npgsql;
using TechMES.Application.Runtime;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.Infrastructure.PostgreSql.Runtime;

/// <summary>
/// Lease хранится в общей Main DB и изменяется под блокировкой одной строки.
/// Соединение не удерживается между проверками. Расширения PostgreSQL не требуются.
/// </summary>
public sealed class PostgreSqlRuntimeLeadershipStore(MainDatabaseFailover database) : IRuntimeLeadershipStore
{
    private bool _schemaReady;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS public.techmes_runtime_lease
        (
            scope text PRIMARY KEY,
            owner_node text NULL,
            owner_instance text NULL,
            owner_role text NULL,
            lease_id uuid NULL,
            expires_at timestamptz NOT NULL DEFAULT '-infinity',
            primary_instance text NULL,
            primary_seen timestamptz NULL,
            primary_successes integer NOT NULL DEFAULT 0
        );
        """;

    public async Task<RuntimeLeadershipSnapshot> TouchAsync(RuntimeLeadershipRequest request, CancellationToken ct)
    {
        try
        {
            await using var connection = await database.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            if (!_schemaReady)
            {
                // Два одновременно стартующих Runtime не создают таблицу параллельно.
                await ExecuteAsync("SELECT pg_advisory_xact_lock(1414352197, 1);", connection, transaction, request, ct);
                await ExecuteAsync(SchemaSql, connection, transaction, request, ct);
            }

            await ExecuteAsync("INSERT INTO public.techmes_runtime_lease(scope) VALUES (@scope) ON CONFLICT DO NOTHING;", connection, transaction, request, ct);
            await ExecuteAsync("SELECT scope FROM public.techmes_runtime_lease WHERE scope = @scope FOR UPDATE;", connection, transaction, request, ct);

            if (request.Role == "Primary")
            {
                var sql = request.AdvertisePrimary ? """
                    UPDATE public.techmes_runtime_lease
                    SET primary_successes = CASE WHEN primary_instance = @instance AND primary_seen > clock_timestamp() - make_interval(secs => @fresh) THEN LEAST(primary_successes + 1, @recovery) ELSE 1 END,
                        primary_instance = @instance,
                        primary_seen = clock_timestamp()
                    WHERE scope = @scope AND (primary_instance IS NULL OR primary_instance = @instance OR primary_seen <= clock_timestamp() - make_interval(secs => @fresh));
                    """ : """
                    UPDATE public.techmes_runtime_lease
                    SET primary_instance = NULL, primary_seen = NULL, primary_successes = 0
                    WHERE scope = @scope AND primary_instance = @instance;
                    """;

                await ExecuteAsync(sql, connection, transaction, request, ct);
            }

            // Действующий владелец продлевает прежний lease.
            // Истёкший lease всегда получает новый UUID.
            const string claimSql = """
                UPDATE public.techmes_runtime_lease
                SET lease_id = CASE WHEN owner_instance = @instance AND expires_at > clock_timestamp() THEN lease_id ELSE @new_lease END,
                    owner_node = @node,
                    owner_instance = @instance,
                    owner_role = @role,
                    expires_at = clock_timestamp() + make_interval(secs => @lease_seconds)
                WHERE scope = @scope AND
                (
                    (owner_instance = @instance AND expires_at > clock_timestamp())
                    OR
                    (@can_acquire AND expires_at <= clock_timestamp() AND
                        (@role = 'Primary' OR primary_seen IS NULL OR primary_seen <= clock_timestamp() - make_interval(secs => @fresh)))
                );
                """;

            await ExecuteAsync(claimSql, connection, transaction, request, ct);

            const string readSql = """
                SELECT owner_instance = @instance AND expires_at > clock_timestamp() AS is_owner,
                       owner_instance = @instance AND owner_role = 'Secondary' AND primary_successes >= @recovery AND primary_seen > clock_timestamp() - make_interval(secs => @fresh) AS yield_to_primary,
                       lease_id, owner_node, owner_instance, owner_role,
                       CASE WHEN lease_id IS NULL THEN NULL ELSE expires_at END
                FROM public.techmes_runtime_lease WHERE scope = @scope;
                """;

            RuntimeLeadershipSnapshot snapshot;

            await using (var command = CreateCommand(readSql, connection, transaction, request))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Runtime coordination row is missing.");

                snapshot = new RuntimeLeadershipSnapshot(!reader.IsDBNull(0) && reader.GetBoolean(0), !reader.IsDBNull(1) && reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetDateTime(6)));
            }

            await transaction.CommitAsync(ct);
            _schemaReady = true;

            return snapshot;
        }
        catch
        {
            // После смены БД или ошибки следующая попытка повторно проверит служебную таблицу.
            _schemaReady = false;
            throw;
        }
    }

    /// <summary>
    /// Освобождение выполняется только после завершения рабочих операций прежнего владельца.
    /// Чужой или уже обновлённый lease этот запрос не затрагивает.
    /// </summary>
    public async Task ReleaseAsync(string scope, string instanceId, Guid leaseId, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE public.techmes_runtime_lease
            SET owner_node = NULL, owner_instance = NULL, owner_role = NULL, lease_id = NULL, expires_at = clock_timestamp(),
                primary_seen = CASE WHEN primary_instance = @instance THEN NULL ELSE primary_seen END,
                primary_successes = CASE WHEN primary_instance = @instance THEN 0 ELSE primary_successes END,
                primary_instance = CASE WHEN primary_instance = @instance THEN NULL ELSE primary_instance END
            WHERE scope = @scope AND owner_instance = @instance AND lease_id = @lease;
            """, connection);

        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("instance", instanceId);
        command.Parameters.AddWithValue("lease", leaseId);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeLeadershipRequest request, CancellationToken ct)
    {
        await using var command = CreateCommand(sql, connection, transaction, request);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeLeadershipRequest request)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope", request.Scope);
        command.Parameters.AddWithValue("node", request.NodeId);
        command.Parameters.AddWithValue("instance", request.InstanceId);
        command.Parameters.AddWithValue("role", request.Role);
        command.Parameters.AddWithValue("can_acquire", request.CanAcquire);
        command.Parameters.AddWithValue("new_lease", Guid.NewGuid());
        command.Parameters.AddWithValue("lease_seconds", request.LeaseSeconds);
        command.Parameters.AddWithValue("fresh", request.PrimaryFreshSeconds);
        command.Parameters.AddWithValue("recovery", request.RecoveryThreshold);

        return command;
    }
}