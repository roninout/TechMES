using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using TechMES.Application.Info;
using TechMES.Contracts.Info;

namespace TechMES.Infrastructure.PostgreSql.Info;

public sealed class PostgreSqlEquipmentInfoStore : IEquipmentInfoStore
{
    private const string InfoTable = "public.equip_info";
    private const string NoteTable = "public.equip_note";
    private const string PhotoLinkTable = "public.equip_info_photo";
    private const string InstructionLinkTable = "public.equip_info_instruction";
    private const string SchemeLinkTable = "public.equip_info_scheme";

    private readonly string _connectionString;

    public PostgreSqlEquipmentInfoStore(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException(
                "PostgreSQL connection string is not configured.");
    }

    public async Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);

        var model = new EquipmentInfoDto
        {
            EquipName = equipName
        };

        if (string.IsNullOrWhiteSpace(equipName))
            return model;

        await using var conn = await OpenConnectionAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT
                equip_name,
                product_code,
                supplier,
                description,
                updated_at
            FROM {InfoTable}
            WHERE equip_name = @equip_name
            LIMIT 1;
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("equip_name", equipName);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                model.EquipName = ReadString(reader, "equip_name") ?? equipName;
                model.ProductCode = ReadString(reader, "product_code");
                model.Supplier = ReadString(reader, "supplier");
                model.Description = ReadString(reader, "description");
                model.UpdatedAt = ReadNullableDateTime(reader, "updated_at");
            }
        }

        await LoadSupplierLogoAsync(conn, model, ct);

        model.PhotoCount = await CountLinksAsync(conn, PhotoLinkTable, equipName, ct);
        model.InstructionCount = await CountLinksAsync(conn, InstructionLinkTable, equipName, ct);
        model.SchemeCount = await CountLinksAsync(conn, SchemeLinkTable, equipName, ct);
        model.Notes = await GetNotesAsync(conn, equipName, ct);

        return model;
    }

    public async Task<EquipmentInfoDto> SaveDescriptionAsync(
        string equipName,
        string? description,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);
        if (string.IsNullOrWhiteSpace(equipName))
            throw new InvalidOperationException("Equipment name is empty.");

        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {InfoTable}
            (
                equip_name,
                description,
                updated_at
            )
            VALUES
            (
                @equip_name,
                @description,
                now()
            )
            ON CONFLICT (equip_name)
            DO UPDATE SET
                description = EXCLUDED.description,
                updated_at = now();
            """,
            conn);

        cmd.Parameters.AddWithValue("equip_name", equipName);
        AddNullableText(cmd, "description", description);

        await cmd.ExecuteNonQueryAsync(ct);

        return await GetAsync(equipName, ct);
    }

    public async Task<EquipmentInfoNoteDto> AddNoteAsync(
        string equipName,
        string noteText,
        string userName,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);
        noteText = NormalizeText(noteText);
        userName = NormalizeUser(userName);

        if (string.IsNullOrWhiteSpace(equipName))
            throw new InvalidOperationException("Equipment name is empty.");

        if (string.IsNullOrWhiteSpace(noteText))
            throw new InvalidOperationException("Note text is empty.");

        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await EnsureInfoRowExistsAsync(conn, tx, equipName, ct);

            await using var cmd = new NpgsqlCommand(
                $"""
                INSERT INTO {NoteTable}
                (
                    equip_name,
                    note_text,
                    created_by,
                    created_at
                )
                VALUES
                (
                    @equip_name,
                    @note_text,
                    @created_by,
                    now()
                )
                RETURNING
                    id,
                    equip_name,
                    note_text,
                    created_by,
                    created_at,
                    updated_by,
                    updated_at;
                """,
                conn,
                tx);

            cmd.Parameters.AddWithValue("equip_name", equipName);
            cmd.Parameters.AddWithValue("note_text", noteText);
            cmd.Parameters.AddWithValue("created_by", userName);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Created note was not returned by PostgreSQL.");

            var note = ReadNote(reader);
            await reader.CloseAsync();
            await tx.CommitAsync(ct);

            return note;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<EquipmentInfoNoteDto?> UpdateNoteAsync(
        string equipName,
        long noteId,
        string noteText,
        string userName,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);
        noteText = NormalizeText(noteText);
        userName = NormalizeUser(userName);

        if (string.IsNullOrWhiteSpace(equipName))
            throw new InvalidOperationException("Equipment name is empty.");

        if (noteId <= 0)
            throw new InvalidOperationException("Note id is empty.");

        if (string.IsNullOrWhiteSpace(noteText))
            throw new InvalidOperationException("Note text is empty.");

        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {NoteTable}
            SET
                note_text = @note_text,
                updated_by = @updated_by,
                updated_at = now()
            WHERE id = @id
              AND equip_name = @equip_name
            RETURNING
                id,
                equip_name,
                note_text,
                created_by,
                created_at,
                updated_by,
                updated_at;
            """,
            conn);

        cmd.Parameters.AddWithValue("id", noteId);
        cmd.Parameters.AddWithValue("equip_name", equipName);
        cmd.Parameters.AddWithValue("note_text", noteText);
        cmd.Parameters.AddWithValue("updated_by", userName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadNote(reader)
            : null;
    }

    public async Task DeleteNoteAsync(
        string equipName,
        long noteId,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);
        if (string.IsNullOrWhiteSpace(equipName) || noteId <= 0)
            return;

        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {NoteTable}
            WHERE id = @id
              AND equip_name = @equip_name;
            """,
            conn);

        cmd.Parameters.AddWithValue("id", noteId);
        cmd.Parameters.AddWithValue("equip_name", equipName);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task EnsureInfoRowExistsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string equipName,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {InfoTable}
            (
                equip_name,
                updated_at
            )
            VALUES
            (
                @equip_name,
                now()
            )
            ON CONFLICT (equip_name)
            DO NOTHING;
            """,
            conn,
            tx);

        cmd.Parameters.AddWithValue("equip_name", equipName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> CountLinksAsync(
        NpgsqlConnection conn,
        string tableName,
        string equipName,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT count(*)
            FROM {tableName}
            WHERE equip_name = @equip_name;
            """,
            conn);

        cmd.Parameters.AddWithValue("equip_name", equipName);

        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    private static async Task<List<EquipmentInfoNoteDto>> GetNotesAsync(
        NpgsqlConnection conn,
        string equipName,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
                id,
                equip_name,
                note_text,
                created_by,
                created_at,
                updated_by,
                updated_at
            FROM {NoteTable}
            WHERE equip_name = @equip_name
            ORDER BY created_at DESC, id DESC;
            """,
            conn);

        cmd.Parameters.AddWithValue("equip_name", equipName);

        var result = new List<EquipmentInfoNoteDto>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadNote(reader));
        }

        return result;
    }

    private static async Task LoadSupplierLogoAsync(
        NpgsqlConnection conn,
        EquipmentInfoDto model,
        CancellationToken ct)
    {
        var productCode = NormalizeText(model.ProductCode);
        var supplier = NormalizeText(model.Supplier);

        if (string.IsNullOrWhiteSpace(productCode) && string.IsNullOrWhiteSpace(supplier))
            return;

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
                s.logo_file_name,
                s.logo_data
            FROM public.equip_supplier s
            LEFT JOIN public.equip_order o
                ON o.supplier_id = s.id
            WHERE
                (
                    @product_code <> ''
                    AND o.product_code = @product_code
                )
                OR
                (
                    @supplier_name <> ''
                    AND lower(s.name) = lower(@supplier_name)
                )
            ORDER BY
                CASE WHEN o.product_code = @product_code THEN 0 ELSE 1 END,
                s.name
            LIMIT 1;
            """,
            conn);

        cmd.Parameters.AddWithValue("product_code", productCode);
        cmd.Parameters.AddWithValue("supplier_name", supplier);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;

        var fileName = reader.IsDBNull(0) ? "supplier-logo.png" : reader.GetString(0);
        if (reader.IsDBNull(1))
            return;

        var data = reader.GetFieldValue<byte[]>(1);
        if (data.Length == 0)
            return;

        model.SupplierLogoFileName = fileName;
        model.SupplierLogoDataUrl =
            $"data:{GetImageContentType(fileName)};base64,{Convert.ToBase64String(data)}";
    }

    private static EquipmentInfoNoteDto ReadNote(NpgsqlDataReader reader)
    {
        return new EquipmentInfoNoteDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            EquipName = reader.GetString(reader.GetOrdinal("equip_name")),
            NoteText = reader.GetString(reader.GetOrdinal("note_text")),
            CreatedBy = reader.GetString(reader.GetOrdinal("created_by")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
            UpdatedBy = ReadString(reader, "updated_by"),
            UpdatedAt = ReadNullableDateTime(reader, "updated_at")
        };
    }

    private static string? ReadString(NpgsqlDataReader reader, string columnName)
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

    private static void AddNullableText(NpgsqlCommand cmd, string name, string? value)
    {
        var parameter = cmd.Parameters.Add(name, NpgsqlDbType.Text);
        parameter.Value = string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();
    }

    private static string NormalizeName(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeText(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeUser(string? value)
    {
        value = NormalizeText(value);
        return string.IsNullOrWhiteSpace(value)
            ? "Unknown"
            : value;
    }

    private static string GetImageContentType(string? fileName)
    {
        return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/png"
        };
    }
}
