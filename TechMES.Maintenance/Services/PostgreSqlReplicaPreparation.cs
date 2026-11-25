using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Npgsql.Replication;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

internal sealed class PostgreSqlReplicaPreparation
{
    internal const string ReplicationUser = "techmes_repl";

    internal sealed record Node(string Id, string Version, int VersionNumber, bool Standby, bool ReadOnly, string DataDirectory, string HbaFile, string WalLevel, int FreeSenders, int FreeSlots, int Senders, string Databases, string Receiver, string Sender, string Slot, string ReplicationRole);

    /// <summary>Проверяет, что профиль и его IPv4 относятся к текущему компьютеру перед локальной операцией.</summary>
    internal static void RequireLocal(PostgreSqlSetupProfile profile, string address)
    {
        profile.Validate();
        PostgreSqlNetworkSetupService.ValidateAddress(address);
        var localAddress = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(entry => entry.Address.ToString() == address);
        if (!localAddress || !PostgreSqlSetupService.IsLocal(profile))
            throw new InvalidOperationException($"Откройте Maintenance на сервере {address} и проверьте имя компьютера в профиле.");
    }

    /// <summary>Создаёт отдельное подключение к базе postgres с короткими таймаутами и без пула.</summary>
    private static NpgsqlConnectionStringBuilder Settings(string address, int port, string user, string password) =>
        new() { Host = address, Port = port, Database = "postgres", Username = user, Password = password,
            Timeout = 5, CommandTimeout = 10, Pooling = false, ApplicationName = "TechMES.ReplicaSetup" };

    /// <summary>Читает снимок кластера с указанного адреса и сверяет каталог данных с профилем.</summary>
    internal static async Task<Node> CheckNodeAsync(string address, PostgreSqlSetupProfile profile, string user, string password)
    {
        await using var connection = new NpgsqlConnection(Settings(address, profile.Port, user, password).ConnectionString);
        await connection.OpenAsync();
        return await ReadNodeAsync(connection, profile);
    }

    /// <summary>Разрешает подготовку только на PostgreSQL 18 Primary с ожидаемым идентификатором и доступом на запись.</summary>
    internal static void RequireWritablePrimary(Node node, string expectedId)
    {
        if (node.VersionNumber is < 180000 or >= 190000 || node.Standby || node.ReadOnly ||
            node.Id != expectedId)
            throw new InvalidOperationException("SQL-сервер должен быть доступным для записи PostgreSQL 18 Primary с указанным ID кластера.");
    }

    /// <summary>Читает состояние WAL, список баз и состояние роли репликации без выполнения операций записи.</summary>
    private static async Task<Node> ReadNodeAsync(NpgsqlConnection connection, PostgreSqlSetupProfile profile)
    {
        const string query = """
            SELECT (SELECT system_identifier::text FROM pg_control_system()),
                current_setting('server_version'), current_setting('server_version_num')::int,
                pg_is_in_recovery(), current_setting('default_transaction_read_only')::boolean,
                current_setting('data_directory'), current_setting('hba_file'), current_setting('wal_level'),
                current_setting('max_wal_senders')::int - (SELECT count(*)::int FROM pg_stat_replication),
                current_setting('max_replication_slots')::int - (SELECT count(*)::int FROM pg_replication_slots),
                (SELECT count(*)::int FROM pg_stat_replication),
                COALESCE((SELECT string_agg(format('%s (%s)', datname, pg_size_pretty(pg_database_size(oid))), ', ' ORDER BY datname)
                    FROM pg_database WHERE NOT datistemplate), ''),
                COALESCE((SELECT status FROM pg_stat_wal_receiver), ''),
                COALESCE((SELECT sender_host FROM pg_stat_wal_receiver), ''),
                COALESCE((SELECT slot_name FROM pg_stat_wal_receiver), ''),
                COALESCE((SELECT CASE WHEN rolcanlogin AND rolreplication AND NOT rolsuper THEN 'LOGIN, REPLICATION'
                    ELSE 'проверьте атрибуты существующей роли' END FROM pg_roles WHERE rolname = 'techmes_repl'), 'отсутствует')
            """;

        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidDataException("PostgreSQL вернул пустой результат.");

        var dataDirectory = reader.GetString(5);
        if (!SamePath(dataDirectory, profile.DataDirectory))
            throw new InvalidDataException($"SQL использует каталог {dataDirectory}, в профиле указан {profile.DataDirectory}.");

        return new Node(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3),
            reader.GetBoolean(4), dataDirectory, reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetString(11), reader.GetString(12), reader.GetString(13),
            reader.GetString(14), reader.GetString(15));
    }

    /// <summary>Сравнивает пути PGDATA после нормализации разделителей, регистра и завершающего слеша.</summary>
    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Проверяет физический вход роли репликации командой IDENTIFY_SYSTEM и возвращает ID кластера.</summary>
    internal static async Task<string> IdentifyPrimaryAsync(string address, int port, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Введите пароль techmes_repl для проверки физического подключения.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new PhysicalReplicationConnection(Settings(address, port, ReplicationUser, password).ConnectionString);
        await connection.Open(timeout.Token);
        return (await connection.IdentifySystem(timeout.Token)).SystemId;
    }

    /// <summary>Создаёт роль репликации один раз. Возвращает новый пароль только после успешной фиксации транзакции.</summary>
    internal static async Task<string?> CreateRoleAsync(PostgreSqlSetupProfile profile, string source, string expectedId, string adminUser, string adminPassword)
    {
        RequireLocal(profile, source);
        await using var connection = new NpgsqlConnection(Settings("127.0.0.1", profile.Port, adminUser, adminPassword).ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(4818635203170331)", connection, transaction);
        await command.ExecuteNonQueryAsync();
        var node = await ReadNodeAsync(connection, profile);
        RequireWritablePrimary(node, expectedId);
        if (node.ReplicationRole == "LOGIN, REPLICATION")
            return null;
        if (node.ReplicationRole != "отсутствует")
            throw new InvalidOperationException("Роль techmes_repl уже существует с другими атрибутами. Проверьте её с администратором PostgreSQL.");

        // ASCII-пароль генерируется локально. В DDL передаётся только SCRAM verifier.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        command.CommandText = $"CREATE ROLE techmes_repl WITH LOGIN REPLICATION PASSWORD '{CreateVerifier(password)}'";
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return password;
    }

    /// <summary>Строит SCRAM-SHA-256 verifier для локально сгенерированного ASCII-пароля.</summary>
    private static string CreateVerifier(string password)
    {
        const int iterations = 4096;
        var salt = RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var client = HMACSHA256.HashData(salted, "Client Key"u8);
            var server = HMACSHA256.HashData(salted, "Server Key"u8);
            return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(SHA256.HashData(client))}:{Convert.ToBase64String(server)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salted);
        }
    }

    /// <summary>Добавляет правило SCRAM для одного Secondary. При ошибке восстанавливает исходный файл, если его больше никто не менял.</summary>
    internal static async Task<string?> AddReplicationRuleAsync(PostgreSqlSetupProfile profile, string source, string target, string expectedId, string adminUser, string adminPassword)
    {
        RequireLocal(profile, source);
        PostgreSqlNetworkSetupService.ValidateAddress(target);
        if (source == target)
            throw new InvalidOperationException("IPv4 Primary и Secondary должны различаться.");

        await using var connection = new NpgsqlConnection(Settings("127.0.0.1", profile.Port, adminUser, adminPassword).ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(4818635203170331)", connection, transaction);
        await command.ExecuteNonQueryAsync();
        var node = await ReadNodeAsync(connection, profile);
        RequireWritablePrimary(node, expectedId);
        if (node.ReplicationRole != "LOGIN, REPLICATION")
            throw new InvalidOperationException("Сначала подготовьте роль techmes_repl.");

        command.CommandText = "SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL";
        if ((long)(await command.ExecuteScalarAsync())! != 0)
            throw new InvalidDataException($"Исправьте ошибки текущего файла {node.HbaFile} перед добавлением правила.");

        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_hba_file_rules
              WHERE type IN ('host', 'hostssl') AND database = ARRAY['replication']::text[]
                AND user_name = ARRAY['techmes_repl']::text[] AND address = @target
                AND netmask = '255.255.255.255' AND auth_method = 'scram-sha-256')
            """;
        command.Parameters.AddWithValue("target", target);
        if ((bool)(await command.ExecuteScalarAsync())!)
            return null;
        command.Parameters.Clear();

        // Добавляем правило в конец, сохраняя действие более ранних ограничений.
        var path = Path.GetFullPath(node.HbaFile);
        if (!Path.IsPathFullyQualified(node.HbaFile) ||
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Проверьте локальный путь pg_hba.conf.");

        var temporary = path + $".techmes-{Guid.NewGuid():N}.tmp";
        var backup = path + $".techmes-before-{Guid.NewGuid():N}";
        var original = await File.ReadAllBytesAsync(path);
        if (Array.IndexOf(original, (byte)0) >= 0)
            throw new InvalidDataException("Файл pg_hba.conf должен использовать ASCII-совместимую кодировку.");
        var added = Encoding.ASCII.GetBytes($"\r\nhost    replication    techmes_repl    {target}/32    scram-sha-256\r\n");
        byte[] revised = [..original, ..added];
        var replaced = false;
        try
        {
            await File.WriteAllBytesAsync(temporary, revised);
            if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(original))
                throw new IOException("pg_hba.conf изменился во время подготовки. Повторите операцию.");
            // Замена оставляет исходный файл рядом для восстановления при ошибке.
            File.Replace(temporary, path, backup);
            replaced = true;

            command.CommandText = "SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL";
            if ((long)(await command.ExecuteScalarAsync())! != 0)
                throw new InvalidDataException("PostgreSQL обнаружил ошибку в новом pg_hba.conf.");
            command.CommandText = "SELECT pg_reload_conf()";
            if ((bool)(await command.ExecuteScalarAsync())! == false)
                throw new InvalidOperationException("PostgreSQL не подтвердил перечитывание конфигурации.");
            return backup;
        }
        catch
        {
            if (replaced && (await File.ReadAllBytesAsync(path)).SequenceEqual(revised))
            {
                File.Copy(backup, temporary);
                File.Replace(temporary, path, null);
                command.CommandText = "SELECT pg_reload_conf()";
                await command.ExecuteScalarAsync();
            }
            throw;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
