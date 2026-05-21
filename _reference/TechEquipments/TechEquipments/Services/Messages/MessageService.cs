using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class MessageService : IMessageService
    {
        private readonly IDbContextFactory<PgInfoDbContext> _dbFactory;

        private const string SchemaName = "public";

        private readonly string _qualifiedMessageTable;
        private readonly string _qualifiedMessageViewTable;

        public MessageService(IDbContextFactory<PgInfoDbContext> dbFactory)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

            _qualifiedMessageTable = Qualify("equip_message");
            _qualifiedMessageViewTable = Qualify("equip_message_view");
        }

        public async Task EnsureTableAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var sqlMessage = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedMessageTable}
                (
                    id               bigserial PRIMARY KEY,
                    message_type     text NOT NULL DEFAULT 'Info',
                    message_subject  text NOT NULL DEFAULT '',
                    message_text     text NOT NULL,
                    is_active        boolean NOT NULL DEFAULT true,
                    created_by       text NOT NULL,
                    created_at       timestamp NOT NULL DEFAULT now(),
                    updated_by       text NULL,
                    updated_at       timestamp NULL
                );";

            var sqlMessageView = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedMessageViewTable}
                (
                    message_id   bigint NOT NULL REFERENCES {_qualifiedMessageTable}(id) ON DELETE CASCADE,
                    device_name  text NOT NULL,
                    viewed_at    timestamp NOT NULL DEFAULT now(),

                    PRIMARY KEY (message_id, device_name)
                );";

            await db.Database.ExecuteSqlRawAsync(sqlMessage, ct);
            await db.Database.ExecuteSqlRawAsync(sqlMessageView, ct);

            // На случай если таблица уже была создана в предыдущей версии.
            await db.Database.ExecuteSqlRawAsync(
                $@"ALTER TABLE {_qualifiedMessageTable}
           ADD COLUMN IF NOT EXISTS message_subject text NOT NULL DEFAULT '';", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_message_active_created
           ON {_qualifiedMessageTable} (is_active, created_at DESC, id DESC);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_message_view_device
           ON {_qualifiedMessageViewTable} (device_name, message_id);", ct);
        }

        public async Task<IReadOnlyList<EquipmentMessageDto>> GetMessagesAsync(bool includeInactive, string deviceName, CancellationToken ct = default)
        {
            deviceName = (deviceName ?? "").Trim();

            var result = new List<EquipmentMessageDto>();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
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

                    -- Текущий ПК просмотрел чужое сообщение.
                    EXISTS
                    (
                        SELECT 1
                        FROM {_qualifiedMessageViewTable} v
                        WHERE v.message_id = m.id
                          AND lower(v.device_name) = lower(@device_name)
                          AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                    ) AS is_viewed_by_current_device,

                    -- Текущий ПК является автором, и это сообщение просмотрел кто-то другой.
                    (
                        lower(COALESCE(m.created_by, '')) = lower(@device_name)
                        AND EXISTS
                        (
                            SELECT 1
                            FROM {_qualifiedMessageViewTable} v
                            WHERE v.message_id = m.id
                              AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                        )
                    ) AS is_viewed_by_other_device,

                    -- Список только реальных просмотревших, без автора.
                    COALESCE
                    (
                        (
                            SELECT string_agg(v.device_name, ', ' ORDER BY v.device_name)
                            FROM {_qualifiedMessageViewTable} v
                            WHERE v.message_id = m.id
                              AND btrim(COALESCE(v.device_name, '')) <> ''
                              AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                        ),
                        ''
                    ) AS viewed_by
                FROM {_qualifiedMessageTable} m
                WHERE (@include_inactive = true OR m.is_active = true)
                ORDER BY
                    m.is_active DESC,
                    m.created_at DESC,
                    m.id DESC;";

            AddParameter(cmd, "@include_inactive", includeInactive);
            AddParameter(cmd, "@device_name", deviceName);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var item = ReadMessage(reader);
                item.AcceptChanges();
                result.Add(item);
            }

            return result;
        }

        public async Task<int> GetActiveMessageCountAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                SELECT COUNT(*)
                FROM {_qualifiedMessageTable}
                WHERE is_active = true;";

            var value = await cmd.ExecuteScalarAsync(ct);

            return value == null || value == DBNull.Value
                ? 0
                : Convert.ToInt32(value);
        }

        public async Task<EquipmentMessageDto> SaveMessageAsync(EquipmentMessageDto message, string userName, string deviceName, CancellationToken ct = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            userName = NormalizeUser(userName);
            deviceName = NormalizeUser(deviceName);

            var subject = (message.MessageSubject ?? "").Trim();
            var text = (message.MessageText ?? "").Trim();

            if (string.IsNullOrWhiteSpace(subject))
                throw new InvalidOperationException("Message subject is empty.");

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Message text is empty.");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            long id;

            if (message.Id <= 0)
            {
                using var cmd = conn.CreateCommand();

                cmd.CommandText = $@"
                    INSERT INTO {_qualifiedMessageTable}
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
                        now()
                    )
                    RETURNING id;";

                AddParameter(cmd, "@message_type", message.MessageType.ToString());
                AddParameter(cmd, "@message_subject", subject);
                AddParameter(cmd, "@message_text", text);
                AddParameter(cmd, "@created_by", userName);

                var value = await cmd.ExecuteScalarAsync(ct);
                id = Convert.ToInt64(value);
            }
            else
            {
                id = message.Id;

                using var cmd = conn.CreateCommand();

                cmd.CommandText = $@"
                    UPDATE {_qualifiedMessageTable}
                    SET
                        message_type = @message_type,
                        message_subject = @message_subject,
                        message_text = @message_text,
                        updated_by = @updated_by,
                        updated_at = now()
                    WHERE id = @id;";

                AddParameter(cmd, "@id", id);
                AddParameter(cmd, "@message_type", message.MessageType.ToString());
                AddParameter(cmd, "@message_subject", subject);
                AddParameter(cmd, "@message_text", text);
                AddParameter(cmd, "@updated_by", userName);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            var saved = await GetMessageByIdAsync(id, deviceName, ct);
            if (saved == null)
                throw new InvalidOperationException("Saved message was not found.");

            return saved;
        }

        public async Task<bool> ToggleActivityAsync(long messageId, string userName, CancellationToken ct = default)
        {
            if (messageId <= 0)
                return false;

            userName = NormalizeUser(userName);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                UPDATE {_qualifiedMessageTable}
                SET
                    is_active = NOT is_active,
                    updated_by = @updated_by,
                    updated_at = now()
                WHERE id = @id
                  AND lower(created_by) = lower(@updated_by);";

            AddParameter(cmd, "@id", messageId);
            AddParameter(cmd, "@updated_by", userName);

            var rows = await cmd.ExecuteNonQueryAsync(ct);

            return rows > 0;
        }

        public async Task DeleteMessageAsync(long messageId, CancellationToken ct = default)
        {
            if (messageId <= 0)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                DELETE FROM {_qualifiedMessageTable}
                WHERE id = @id;";

            AddParameter(cmd, "@id", messageId);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task MarkViewedAsync(long messageId, string deviceName, CancellationToken ct = default)
        {
            if (messageId <= 0)
                return;

            deviceName = NormalizeUser(deviceName);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                INSERT INTO {_qualifiedMessageViewTable}
                (
                    message_id,
                    device_name,
                    viewed_at
                )
                VALUES
                (
                    @message_id,
                    @device_name,
                    now()
                )
                ON CONFLICT (message_id, device_name)
                DO UPDATE SET
                    viewed_at = EXCLUDED.viewed_at;";

            AddParameter(cmd, "@message_id", messageId);
            AddParameter(cmd, "@device_name", deviceName);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task<EquipmentMessageDto?> GetMessageByIdAsync(long messageId, string deviceName, CancellationToken ct)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
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

                    -- Текущий ПК просмотрел чужое сообщение.
                    EXISTS
                    (
                        SELECT 1
                        FROM {_qualifiedMessageViewTable} v
                        WHERE v.message_id = m.id
                          AND lower(v.device_name) = lower(@device_name)
                          AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                    ) AS is_viewed_by_current_device,

                    -- Текущий ПК является автором, и это сообщение просмотрел кто-то другой.
                    (
                        lower(COALESCE(m.created_by, '')) = lower(@device_name)
                        AND EXISTS
                        (
                            SELECT 1
                            FROM {_qualifiedMessageViewTable} v
                            WHERE v.message_id = m.id
                              AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                        )
                    ) AS is_viewed_by_other_device,

                    COALESCE
                    (
                        (
                            SELECT string_agg(v.device_name, ', ' ORDER BY v.device_name)
                            FROM {_qualifiedMessageViewTable} v
                            WHERE v.message_id = m.id
                              AND btrim(COALESCE(v.device_name, '')) <> ''
                              AND lower(v.device_name) <> lower(COALESCE(m.created_by, ''))
                        ),
                        ''
                    ) AS viewed_by
                FROM {_qualifiedMessageTable} m
                WHERE m.id = @id
                LIMIT 1;";

            AddParameter(cmd, "@id", messageId);
            AddParameter(cmd, "@device_name", deviceName);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return null;

            var item = ReadMessage(reader);
            item.AcceptChanges();

            return item;
        }

        private static EquipmentMessageDto ReadMessage(DbDataReader reader)
        {
            var typeText = reader.IsDBNull(1) ? "Info" : reader.GetString(1);

            if (!Enum.TryParse<MessageType>(typeText, true, out var type))
                type = MessageType.Info;

            return new EquipmentMessageDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                MessageType = type,
                MessageSubject = reader.IsDBNull(2) ? "" : reader.GetString(2),
                MessageText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                IsActive = !reader.IsDBNull(4) && reader.GetBoolean(4),
                CreatedBy = reader.IsDBNull(5) ? "" : reader.GetString(5),
                CreatedAt = reader.IsDBNull(6) ? DateTime.MinValue : reader.GetFieldValue<DateTime>(6),
                UpdatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTime>(8),

                // Текущий ПК просмотрел чужое сообщение.
                IsViewedByCurrentDevice = !reader.IsDBNull(9) && reader.GetBoolean(9),

                // Твое сообщение просмотрел кто-то другой.
                IsViewedByOtherDevice = !reader.IsDBNull(10) && reader.GetBoolean(10),

                ViewedByText = reader.IsDBNull(11) ? "" : reader.GetString(11)
            };
        }

        private static string NormalizeUser(string? value)
        {
            value = (value ?? "").Trim();

            return string.IsNullOrWhiteSpace(value)
                ? "Unknown"
                : value;
        }

        private static async Task EnsureConnectionOpenAsync(DbConnection conn, CancellationToken ct)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);
        }

        private static void AddParameter(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static string Qualify(string tableName)
        {
            return $"{SchemaName}.{tableName}";
        }
    }
}