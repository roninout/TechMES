using Microsoft.Extensions.Configuration;
using Npgsql;
using TechMES.Application.Messages;
using TechMES.Contracts.Messages;

namespace TechMES.Infrastructure.PostgreSql.Messages;

/// <summary>
/// PostgreSQL-адаптер для сообщений.
///
/// Runtime.Service работает не с этим классом напрямую, а с интерфейсом IMessageStore.
/// Благодаря этому позже можно заменить PostgreSQL на другую БД, не переписывая WEB
/// и не меняя HTTP endpoint-ы Runtime.Service.
/// </summary>
public sealed class PostgreSqlMessageStore : IMessageStore
{
    private readonly string _connectionString;

    public PostgreSqlMessageStore(IConfiguration configuration)
    {
        // Строку подключения держим в appsettings Runtime.Service.
        // Сначала пробуем стандартный раздел ConnectionStrings, затем наш раздел Database.
        _connectionString =
            configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Не найдена строка подключения к PostgreSQL. " +
                "Укажите ConnectionStrings:Default или Database:ConnectionString в appsettings.json.");
    }

    /// <summary>
    /// Подготавливаем таблицы Messages.
    /// Метод вызывается один раз при старте Runtime.Service.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Таблица сообщений.
        // message_type храним как text: так проще читать данные вручную в БД
        // и не зависеть от числовых значений enum.
        await using (var cmd = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS public.equip_message
            (
                id              bigserial PRIMARY KEY,
                message_type    text NOT NULL DEFAULT 'Info',
                message_subject text NOT NULL DEFAULT '',
                message_text    text NOT NULL DEFAULT '',
                is_active       boolean NOT NULL DEFAULT true,
                created_by      text NOT NULL DEFAULT '',
                created_at      timestamp without time zone NOT NULL DEFAULT now(),
                updated_by      text NULL,
                updated_at      timestamp without time zone NULL
            );
            """,
            conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Таблица просмотров сообщений.
        // Одно устройство/пользователь должно иметь только одну запись просмотра
        // для одного сообщения.
        await using (var cmd = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS public.equip_message_view
            (
                message_id  bigint NOT NULL REFERENCES public.equip_message(id) ON DELETE CASCADE,
                device_name text NOT NULL,
                viewed_at   timestamp without time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_equip_message_view PRIMARY KEY (message_id, device_name)
            );
            """,
            conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Индексы ускоряют частые операции:
        // - список активных сообщений;
        // - поиск просмотров по сообщению;
        // - поиск просмотров по устройству.
        await ExecuteNonQueryAsync(conn,
            """
            CREATE INDEX IF NOT EXISTS ix_equip_message_active_created
                ON public.equip_message(is_active, created_at DESC);
            """,
            ct);

        await ExecuteNonQueryAsync(conn,
            """
            CREATE INDEX IF NOT EXISTS ix_equip_message_view_device
                ON public.equip_message_view(device_name);
            """,
            ct);

        // Trigger нужен, чтобы watcher видел изменения,
        // сделанные напрямую в PostgreSQL не через Runtime.Service.
        //
        // Например, если кто-то вручную изменит message_text через pgAdmin,
        // PostgreSQL сам обновит updated_at, и MessageStorageWatcher заметит изменение.
        await ExecuteNonQueryAsync(conn,
            """
            CREATE OR REPLACE FUNCTION public.techmes_set_equip_message_updated_at()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                NEW.updated_at = now();
                RETURN NEW;
            END;
            $$;
            """,
            ct);

        await ExecuteNonQueryAsync(conn,
            """
            DROP TRIGGER IF EXISTS tr_equip_message_set_updated_at
            ON public.equip_message;
            """,
            ct);

        await ExecuteNonQueryAsync(conn,
            """
            CREATE TRIGGER tr_equip_message_set_updated_at
            BEFORE UPDATE ON public.equip_message
            FOR EACH ROW
            EXECUTE FUNCTION public.techmes_set_equip_message_updated_at();
            """,
            ct);
    }

    public async Task<IReadOnlyList<EquipmentMessageDto>> GetMessagesAsync(
        bool includeInactive,
        string deviceName,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                m.id,
                m.message_type,
                m.message_subject,
                m.message_text,
                m.is_active,
                m.created_by,
                m.created_at,
                m.updated_by,
                m.updated_at,
                EXISTS
                (
                    SELECT 1
                    FROM public.equip_message_view v
                    WHERE v.message_id = m.id
                      AND v.device_name = @device_name
                      AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                ) AS is_viewed_by_current_device,
                (
                    lower(COALESCE(m.created_by, '')) = lower(@device_name)
                    AND EXISTS
                    (
                        SELECT 1
                        FROM public.equip_message_view v
                        WHERE v.message_id = m.id
                          AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                    )
                ) AS is_viewed_by_other_device,
                COALESCE
                (
                    (
                        SELECT string_agg(x.device_name, ', ' ORDER BY x.device_name)
                        FROM
                        (
                            SELECT DISTINCT v.device_name
                            FROM public.equip_message_view v
                            WHERE v.message_id = m.id
                              AND btrim(COALESCE(v.device_name, '')) <> ''
                              AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                        ) x
                    ),
                    ''
                ) AS viewed_by_text
            FROM public.equip_message m
            WHERE (@include_inactive = true OR m.is_active = true)
            ORDER BY m.is_active DESC, m.created_at DESC, m.id DESC;
            """,
            conn);

        cmd.Parameters.AddWithValue("device_name", NormalizeDeviceName(deviceName));
        cmd.Parameters.AddWithValue("include_inactive", includeInactive);

        var result = new List<EquipmentMessageDto>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadMessage(reader));
        }

        return result;
    }

    public async Task<int> GetActiveMessageCountAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.equip_message
            WHERE is_active = true;
            """,
            conn);

        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    public async Task<MessageStorageSnapshot> GetStorageSnapshotAsync(
    CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                (
                    SELECT count(*)
                    FROM public.equip_message
                ) AS total_message_count,

                (
                    SELECT count(*)
                    FROM public.equip_message
                    WHERE is_active = true
                ) AS active_message_count,

                (
                    SELECT count(*)
                    FROM public.equip_message_view
                ) AS view_count,

                (
                    SELECT max(greatest(created_at, coalesce(updated_at, created_at)))
                    FROM public.equip_message
                ) AS last_message_changed_at,

                (
                    SELECT max(viewed_at)
                    FROM public.equip_message_view
                ) AS last_viewed_at;
            """,
            conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return new MessageStorageSnapshot();

        return new MessageStorageSnapshot
        {
            TotalMessageCount = reader.GetInt64(0),
            ActiveMessageCount = reader.GetInt64(1),
            ViewCount = reader.GetInt64(2),
            LastMessageChangedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            LastViewedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
        };
    }

    public async Task<EquipmentMessageDto> SaveMessageAsync(
        SaveMessageRequest request,
        string userName,
        string deviceName,
        CancellationToken ct = default)
    {
        var normalizedUserName = NormalizeDeviceName(userName);
        var normalizedDeviceName = NormalizeDeviceName(deviceName);

        if (request.Id <= 0)
        {
            var newId = await InsertMessageAsync(request, normalizedUserName, ct);

            return await GetMessageByIdAsync(newId, normalizedDeviceName, ct)
                   ?? throw new InvalidOperationException("Сообщение было создано, но не найдено при повторном чтении.");
        }

        await UpdateMessageAsync(request, normalizedUserName, ct);

        return await GetMessageByIdAsync(request.Id, normalizedDeviceName, ct)
               ?? throw new InvalidOperationException($"Сообщение Id={request.Id} не найдено.");
    }

    public async Task<bool> ToggleActivityAsync(
        long messageId,
        string userName,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            UPDATE public.equip_message
            SET
                is_active = NOT is_active,
                updated_by = @updated_by,
                updated_at = @updated_at
            WHERE id = @id
              AND lower(created_by) = lower(@updated_by);
            """,
            conn);

        cmd.Parameters.AddWithValue("id", messageId);
        cmd.Parameters.AddWithValue("updated_by", NormalizeDeviceName(userName));
        cmd.Parameters.AddWithValue("updated_at", DateTime.Now);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task DeleteMessageAsync(long messageId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            DELETE FROM public.equip_message
            WHERE id = @id;
            """,
            conn);

        cmd.Parameters.AddWithValue("id", messageId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkViewedAsync(long messageId, string deviceName, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var normalizedDeviceName = NormalizeDeviceName(deviceName);
        var now = DateTime.Now;

        // Сначала пробуем обновить существующую отметку просмотра.
        await using (var updateCmd = new NpgsqlCommand(
            """
            UPDATE public.equip_message_view
            SET viewed_at = @viewed_at
            WHERE message_id = @message_id
              AND device_name = @device_name
              AND EXISTS
              (
                  SELECT 1
                  FROM public.equip_message m
                  WHERE m.id = @message_id
                    AND lower(COALESCE(m.created_by, '')) <> lower(@device_name)
              );
            """,
            conn))
        {
            updateCmd.Parameters.AddWithValue("message_id", messageId);
            updateCmd.Parameters.AddWithValue("device_name", normalizedDeviceName);
            updateCmd.Parameters.AddWithValue("viewed_at", now);

            var affected = await updateCmd.ExecuteNonQueryAsync(ct);
            if (affected > 0)
                return;
        }

        // Если отметки ещё нет — добавляем её.
        // INSERT делаем через SELECT WHERE EXISTS, чтобы не получить ошибку,
        // если сообщение уже было удалено другим клиентом.
        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO public.equip_message_view
            (
                message_id,
                device_name,
                viewed_at
            )
            SELECT
                @message_id,
                @device_name,
                @viewed_at
            WHERE EXISTS
            (
                SELECT 1
                FROM public.equip_message
                WHERE id = @message_id
                  AND lower(COALESCE(created_by, '')) <> lower(@device_name)
            );
            """,
            conn))
        {
            insertCmd.Parameters.AddWithValue("message_id", messageId);
            insertCmd.Parameters.AddWithValue("device_name", normalizedDeviceName);
            insertCmd.Parameters.AddWithValue("viewed_at", now);

            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<long> InsertMessageAsync(
        SaveMessageRequest request,
        string userName,
        CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO public.equip_message
            (
                message_type,
                message_subject,
                message_text,
                is_active,
                created_by,
                created_at
            )
            VALUES
            (
                @message_type,
                @message_subject,
                @message_text,
                true,
                @created_by,
                @created_at
            )
            RETURNING id;
            """,
            conn);

        cmd.Parameters.AddWithValue("message_type", request.MessageType.ToString());
        cmd.Parameters.AddWithValue("message_subject", request.MessageSubject.Trim());
        cmd.Parameters.AddWithValue("message_text", request.MessageText.Trim());
        cmd.Parameters.AddWithValue("created_by", userName);
        cmd.Parameters.AddWithValue("created_at", DateTime.Now);

        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    private async Task UpdateMessageAsync(
        SaveMessageRequest request,
        string userName,
        CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            UPDATE public.equip_message
            SET
                message_type = @message_type,
                message_subject = @message_subject,
                message_text = @message_text,
                updated_by = @updated_by,
                updated_at = @updated_at
            WHERE id = @id;
            """,
            conn);

        cmd.Parameters.AddWithValue("id", request.Id);
        cmd.Parameters.AddWithValue("message_type", request.MessageType.ToString());
        cmd.Parameters.AddWithValue("message_subject", request.MessageSubject.Trim());
        cmd.Parameters.AddWithValue("message_text", request.MessageText.Trim());
        cmd.Parameters.AddWithValue("updated_by", userName);
        cmd.Parameters.AddWithValue("updated_at", DateTime.Now);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<EquipmentMessageDto?> GetMessageByIdAsync(
        long id,
        string deviceName,
        CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                m.id,
                m.message_type,
                m.message_subject,
                m.message_text,
                m.is_active,
                m.created_by,
                m.created_at,
                m.updated_by,
                m.updated_at,
                EXISTS
                (
                    SELECT 1
                    FROM public.equip_message_view v
                    WHERE v.message_id = m.id
                      AND v.device_name = @device_name
                      AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                ) AS is_viewed_by_current_device,
                (
                    lower(COALESCE(m.created_by, '')) = lower(@device_name)
                    AND EXISTS
                    (
                        SELECT 1
                        FROM public.equip_message_view v
                        WHERE v.message_id = m.id
                          AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                    )
                ) AS is_viewed_by_other_device,
                COALESCE
                (
                    (
                        SELECT string_agg(x.device_name, ', ' ORDER BY x.device_name)
                        FROM
                        (
                            SELECT DISTINCT v.device_name
                            FROM public.equip_message_view v
                            WHERE v.message_id = m.id
                              AND btrim(COALESCE(v.device_name, '')) <> ''
                              AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                        ) x
                    ),
                    ''
                ) AS viewed_by_text
            FROM public.equip_message m
            WHERE m.id = @id;
            """,
            conn);

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("device_name", NormalizeDeviceName(deviceName));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadMessage(reader)
            : null;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection conn,
        string sql,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static EquipmentMessageDto ReadMessage(NpgsqlDataReader reader)
    {
        return new EquipmentMessageDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            MessageType = ParseMessageType(reader.GetString(reader.GetOrdinal("message_type"))),
            MessageSubject = reader.GetString(reader.GetOrdinal("message_subject")),
            MessageText = reader.GetString(reader.GetOrdinal("message_text")),
            IsActive = reader.GetBoolean(reader.GetOrdinal("is_active")),
            CreatedBy = reader.GetString(reader.GetOrdinal("created_by")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
            UpdatedBy = ReadNullableString(reader, "updated_by"),
            UpdatedAt = ReadNullableDateTime(reader, "updated_at"),
            IsViewedByCurrentDevice = reader.GetBoolean(reader.GetOrdinal("is_viewed_by_current_device")),
            IsViewedByOtherDevice = reader.GetBoolean(reader.GetOrdinal("is_viewed_by_other_device")),
            ViewedByText = reader.GetString(reader.GetOrdinal("viewed_by_text"))
        };
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);
    }

    private static DateTime? ReadNullableDateTime(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetDateTime(ordinal);
    }

    private static MessageType ParseMessageType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return MessageType.Info;

        if (Enum.TryParse<MessageType>(value, ignoreCase: true, out var byName))
            return byName;

        if (int.TryParse(value, out var numeric) && Enum.IsDefined(typeof(MessageType), numeric))
            return (MessageType)numeric;

        return MessageType.Info;
    }

    private static string NormalizeDeviceName(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Environment.MachineName
            : value.Trim();
    }
}
