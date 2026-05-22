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
    private const string PhotoTable = "public.equip_photo";
    private const string InstructionTable = "public.equip_instruction";
    private const string SchemeTable = "public.equip_scheme";
    private const string DocumentViewTable = "public.equip_info_pdf_view";
    private const string FavoriteTable = "public.equip_favorite";

    private readonly string _connectionString;

    public PostgreSqlEquipmentInfoStore(IConfiguration configuration)
    {
        _connectionString =
            configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException(
                "PostgreSQL connection string is not configured.");
    }

    /// <summary>
    /// Loads card fields, supplier logo, linked file metadata, and notes for one
    /// equipment item. File bytes are deliberately not loaded here.
    /// </summary>
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

        model.Photos = await LoadLinkedFilesMetadataAsync(
            conn,
            PhotoLinkTable,
            PhotoTable,
            "photo_id",
            EquipmentInfoFileKind.Photo,
            equipName,
            ct);

        model.Instructions = await LoadLinkedFilesMetadataAsync(
            conn,
            InstructionLinkTable,
            InstructionTable,
            "instruction_id",
            EquipmentInfoFileKind.Instruction,
            equipName,
            ct);

        model.Schemes = await LoadLinkedFilesMetadataAsync(
            conn,
            SchemeLinkTable,
            SchemeTable,
            "scheme_id",
            EquipmentInfoFileKind.Scheme,
            equipName,
            ct);

        model.PhotoCount = model.Photos.Count;
        model.InstructionCount = model.Instructions.Count;
        model.SchemeCount = model.Schemes.Count;
        model.Notes = await GetNotesAsync(conn, equipName, ct);

        return model;
    }

    /// <summary>
    /// Loads only per-equipment counters for tree badges/icons. This keeps the
    /// catalog endpoint lightweight and avoids pulling full Info records.
    /// </summary>
    public async Task<IReadOnlyList<EquipmentInfoSummaryDto>> GetSummariesAsync(
        IEnumerable<string> equipNames,
        CancellationToken ct = default)
    {
        var names = equipNames
            .Select(NormalizeName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
            return [];

        var summaries = names.ToDictionary(
            x => x,
            x => new EquipmentInfoSummaryDto { EquipName = x },
            StringComparer.OrdinalIgnoreCase);

        await using var conn = await OpenConnectionAsync(ct);

        await LoadLinkedFileCountsAsync(
            conn,
            PhotoLinkTable,
            names,
            summaries,
            (summary, count) => summary.PhotoCount = count,
            ct);

        await LoadLinkedFileCountsAsync(
            conn,
            InstructionLinkTable,
            names,
            summaries,
            (summary, count) => summary.InstructionCount = count,
            ct);

        await LoadLinkedFileCountsAsync(
            conn,
            SchemeLinkTable,
            names,
            summaries,
            (summary, count) => summary.SchemeCount = count,
            ct);

        await LoadNoteCountsAsync(conn, names, summaries, ct);

        return summaries.Values.ToList();
    }

    /// <summary>
    /// Upserts the editable description in equip_info and returns the refreshed
    /// Info snapshot for the frontend.
    /// </summary>
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

    public async Task<IReadOnlyCollection<string>> GetFavoriteEquipNamesAsync(
        string deviceName,
        CancellationToken ct = default)
    {
        deviceName = NormalizeUser(deviceName);

        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT equip_name
            FROM {FavoriteTable}
            WHERE device_name = @device_name
            ORDER BY equip_name;
            """,
            conn);

        cmd.Parameters.AddWithValue("device_name", deviceName);

        var result = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var equipName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            if (!string.IsNullOrWhiteSpace(equipName))
                result.Add(equipName);
        }

        return result;
    }

    public async Task SetFavoriteAsync(
        string equipName,
        bool isFavorite,
        string deviceName,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);
        deviceName = NormalizeUser(deviceName);

        if (string.IsNullOrWhiteSpace(equipName))
            throw new InvalidOperationException("Equipment name is empty.");

        await using var conn = await OpenConnectionAsync(ct);

        if (isFavorite)
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                INSERT INTO {FavoriteTable}
                (
                    device_name,
                    equip_name,
                    updated_at
                )
                VALUES
                (
                    @device_name,
                    @equip_name,
                    now()
                )
                ON CONFLICT (device_name, equip_name)
                DO UPDATE SET
                    updated_at = now();
                """,
                conn);

            cmd.Parameters.AddWithValue("device_name", deviceName);
            cmd.Parameters.AddWithValue("equip_name", equipName);

            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }

        await using (var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {FavoriteTable}
            WHERE device_name = @device_name
              AND equip_name = @equip_name;
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("device_name", deviceName);
            cmd.Parameters.AddWithValue("equip_name", equipName);

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Loads binary file content only when the browser asks for it. This is the
    /// server-side half of lazy image/PDF loading and browser cache reuse.
    /// </summary>
    public async Task<EquipmentInfoFileContentDto?> GetFileAsync(
        EquipmentInfoFileKind kind,
        long id,
        CancellationToken ct = default)
    {
        if (id <= 0)
            return null;

        var tableName = GetFileTable(kind);

        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
                id,
                file_name,
                display_name,
                file_hash,
                file_data,
                updated_at
            FROM {tableName}
            WHERE id = @id
            LIMIT 1;
            """,
            conn);

        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var fileName = ReadString(reader, "file_name") ?? string.Empty;

        return new EquipmentInfoFileContentDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Kind = kind,
            FileName = fileName,
            DisplayName = ReadString(reader, "display_name") ?? fileName,
            FileHash = ReadString(reader, "file_hash") ?? string.Empty,
            ContentType = GetContentType(fileName, kind),
            FileData = reader.IsDBNull(reader.GetOrdinal("file_data"))
                ? []
                : reader.GetFieldValue<byte[]>(reader.GetOrdinal("file_data")),
            UpdatedAt = ReadNullableDateTime(reader, "updated_at")
        };
    }

    /// <summary>
    /// Reads remembered page/zoom for one document and equipment pair.
    /// </summary>
    public async Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(
        string equipName,
        EquipmentInfoFileKind kind,
        long fileId,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);

        if (string.IsNullOrWhiteSpace(equipName) || fileId <= 0 || kind == EquipmentInfoFileKind.Photo)
            return null;

        await using var conn = await OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
                equip_name,
                info_page_kind,
                file_id,
                file_name,
                page_number,
                zoom_factor,
                anchor_x,
                anchor_y,
                updated_at
            FROM {DocumentViewTable}
            WHERE equip_name = @equip_name
              AND info_page_kind = @info_page_kind
              AND file_id = @file_id
            LIMIT 1;
            """,
            conn);

        cmd.Parameters.AddWithValue("equip_name", equipName);
        cmd.Parameters.AddWithValue("info_page_kind", kind.ToString());
        cmd.Parameters.AddWithValue("file_id", fileId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadDocumentViewState(reader)
            : null;
    }

    /// <summary>
    /// Upserts remembered page/zoom values for PDF and scheme viewers.
    /// </summary>
    public async Task<EquipmentInfoDocumentViewStateDto> SaveDocumentViewStateAsync(
        string equipName,
        SaveEquipmentInfoDocumentViewStateRequest request,
        CancellationToken ct = default)
    {
        equipName = NormalizeName(equipName);

        if (string.IsNullOrWhiteSpace(equipName))
            throw new InvalidOperationException("Equipment name is empty.");

        if (request.Kind == EquipmentInfoFileKind.Photo)
            throw new InvalidOperationException("Photo does not support PDF view state.");

        if (request.FileId <= 0)
            throw new InvalidOperationException("File id is empty.");

        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await EnsureInfoRowExistsAsync(conn, tx, equipName, ct);

            await using var cmd = new NpgsqlCommand(
                $"""
                INSERT INTO {DocumentViewTable}
                (
                    equip_name,
                    info_page_kind,
                    file_id,
                    file_name,
                    page_number,
                    zoom_factor,
                    anchor_x,
                    anchor_y,
                    updated_at
                )
                VALUES
                (
                    @equip_name,
                    @info_page_kind,
                    @file_id,
                    @file_name,
                    @page_number,
                    @zoom_factor,
                    @anchor_x,
                    @anchor_y,
                    now()
                )
                ON CONFLICT (equip_name, info_page_kind, file_id)
                DO UPDATE SET
                    file_name = EXCLUDED.file_name,
                    page_number = EXCLUDED.page_number,
                    zoom_factor = EXCLUDED.zoom_factor,
                    anchor_x = EXCLUDED.anchor_x,
                    anchor_y = EXCLUDED.anchor_y,
                    updated_at = now()
                RETURNING
                    equip_name,
                    info_page_kind,
                    file_id,
                    file_name,
                    page_number,
                    zoom_factor,
                    anchor_x,
                    anchor_y,
                    updated_at;
                """,
                conn,
                tx);

            cmd.Parameters.AddWithValue("equip_name", equipName);
            cmd.Parameters.AddWithValue("info_page_kind", request.Kind.ToString());
            cmd.Parameters.AddWithValue("file_id", request.FileId);
            cmd.Parameters.AddWithValue("file_name", NormalizeText(request.FileName));
            cmd.Parameters.AddWithValue("page_number", Math.Max(1, request.PageNumber));
            cmd.Parameters.AddWithValue("zoom_factor", Math.Clamp(request.ZoomFactor, 25, 400));
            cmd.Parameters.AddWithValue("anchor_x", request.AnchorX);
            cmd.Parameters.AddWithValue("anchor_y", request.AnchorY);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Saved PDF view state was not returned by PostgreSQL.");

            var state = ReadDocumentViewState(reader);
            await reader.CloseAsync();
            await tx.CommitAsync(ct);

            return state;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Adds one note to the collection tied to the equipment name.
    /// </summary>
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

    /// <summary>
    /// Updates note text and audit fields, returning null if the note disappeared.
    /// </summary>
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

    /// <summary>
    /// Deletes one note. Missing rows are treated as already deleted.
    /// </summary>
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

    /// <summary>
    /// Reads file metadata through the WPF-style link tables without loading blob
    /// data. Counts and tabs are based on this lightweight metadata.
    /// </summary>
    private static async Task<List<EquipmentInfoFileDto>> LoadLinkedFilesMetadataAsync(
        NpgsqlConnection conn,
        string linkTable,
        string libraryTable,
        string linkIdColumn,
        EquipmentInfoFileKind kind,
        string equipName,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
                lib.id,
                lib.file_name,
                lib.display_name,
                lib.file_hash,
                link.sort_order,
                lib.updated_at,
                lib.equip_type_group
            FROM {linkTable} link
            INNER JOIN {libraryTable} lib
                ON lib.id = link.{linkIdColumn}
            WHERE link.equip_name = @equip_name
            ORDER BY link.sort_order, lib.display_name, lib.file_name;
            """,
            conn);

        cmd.Parameters.AddWithValue("equip_name", equipName);

        var result = new List<EquipmentInfoFileDto>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            result.Add(new EquipmentInfoFileDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipName = equipName,
                FileName = fileName,
                DisplayName = reader.IsDBNull(2) ? fileName : reader.GetString(2),
                FileHash = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                SortOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                UpdatedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                EquipTypeGroupKey = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Kind = kind,
                ContentType = GetContentType(fileName, kind)
            });
        }

        return result;
    }

    private static async Task LoadLinkedFileCountsAsync(
        NpgsqlConnection conn,
        string linkTable,
        string[] equipNames,
        Dictionary<string, EquipmentInfoSummaryDto> summaries,
        Action<EquipmentInfoSummaryDto, int> applyCount,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
                equip_name,
                count(*)::int AS item_count
            FROM {linkTable}
            WHERE equip_name = ANY(@equip_names)
            GROUP BY equip_name;
            """,
            conn);

        cmd.Parameters.AddWithValue(
            "equip_names",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            equipNames);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var equipName = ReadString(reader, "equip_name");
            if (string.IsNullOrWhiteSpace(equipName)
                || !summaries.TryGetValue(equipName, out var summary))
            {
                continue;
            }

            applyCount(summary, reader.GetInt32(reader.GetOrdinal("item_count")));
        }
    }

    private static async Task LoadNoteCountsAsync(
        NpgsqlConnection conn,
        string[] equipNames,
        Dictionary<string, EquipmentInfoSummaryDto> summaries,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT
                equip_name,
                count(*)::int AS note_count
            FROM {NoteTable}
            WHERE equip_name = ANY(@equip_names)
              AND nullif(btrim(note_text), '') IS NOT NULL
            GROUP BY equip_name;
            """,
            conn);

        cmd.Parameters.AddWithValue(
            "equip_names",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            equipNames);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var equipName = ReadString(reader, "equip_name");
            if (string.IsNullOrWhiteSpace(equipName)
                || !summaries.TryGetValue(equipName, out var summary))
            {
                continue;
            }

            summary.NoteCount = reader.GetInt32(reader.GetOrdinal("note_count"));
        }
    }

    /// <summary>
    /// Loads the note collection newest-first, matching the card list in the UI.
    /// </summary>
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

    /// <summary>
    /// Embeds supplier logo bytes as a data URL because it is small, belongs to the
    /// card header, and should render together with the equipment summary.
    /// </summary>
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

    private static EquipmentInfoDocumentViewStateDto ReadDocumentViewState(NpgsqlDataReader reader)
    {
        var kindText = ReadString(reader, "info_page_kind") ?? EquipmentInfoFileKind.Instruction.ToString();
        if (!Enum.TryParse<EquipmentInfoFileKind>(kindText, ignoreCase: true, out var kind))
            kind = EquipmentInfoFileKind.Instruction;

        return new EquipmentInfoDocumentViewStateDto
        {
            EquipName = ReadString(reader, "equip_name") ?? string.Empty,
            Kind = kind,
            FileId = reader.GetInt64(reader.GetOrdinal("file_id")),
            FileName = ReadString(reader, "file_name") ?? string.Empty,
            PageNumber = reader.GetInt32(reader.GetOrdinal("page_number")),
            ZoomFactor = reader.GetDouble(reader.GetOrdinal("zoom_factor")),
            AnchorX = reader.GetDouble(reader.GetOrdinal("anchor_x")),
            AnchorY = reader.GetDouble(reader.GetOrdinal("anchor_y")),
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

    private static string GetFileTable(EquipmentInfoFileKind kind)
    {
        return kind switch
        {
            EquipmentInfoFileKind.Photo => PhotoTable,
            EquipmentInfoFileKind.Instruction => InstructionTable,
            EquipmentInfoFileKind.Scheme => SchemeTable,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static string GetContentType(string? fileName, EquipmentInfoFileKind kind)
    {
        if (kind is EquipmentInfoFileKind.Instruction or EquipmentInfoFileKind.Scheme)
            return "application/pdf";

        return GetImageContentType(fileName);
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
