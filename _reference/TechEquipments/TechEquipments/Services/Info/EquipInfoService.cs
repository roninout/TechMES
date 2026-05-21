using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace TechEquipments
{
    /// <summary>
    /// Raw SQL сервис для Info/Favorites.
    /// Работает с отдельной БД srd_db.
    /// </summary>
    public sealed class EquipInfoService : IEquipInfoService
    {
        private readonly IDbContextFactory<PgInfoDbContext> _dbFactory;
        private readonly IConfiguration _config;
        private readonly IAppRuntimeContext _appRuntime;

        private const string SchemaName = "public";

        private readonly string _qualifiedInfoTable;
        private readonly string _qualifiedPhotoTable;
        private readonly string _qualifiedInstructionTable;
        private readonly string _qualifiedSchemeTable;
        private readonly string _qualifiedInfoPhotoLinkTable;
        private readonly string _qualifiedInfoInstructionLinkTable;
        private readonly string _qualifiedInfoSchemeLinkTable;
        private readonly string _qualifiedInfoDocumentViewTable;
        private readonly string _qualifiedFavoriteTable;
        private readonly string _qualifiedSupplierTable;
        private readonly string _qualifiedOrderTable;
        private readonly string _qualifiedNoteTable;

        public EquipInfoService(IDbContextFactory<PgInfoDbContext> dbFactory, IConfiguration config, IAppRuntimeContext appRuntime)
        {
            _dbFactory = dbFactory;
            _config = config;
            _appRuntime = appRuntime;

            _qualifiedInfoTable = Qualify("equip_info");
            _qualifiedPhotoTable = Qualify("equip_photo");
            _qualifiedInstructionTable = Qualify("equip_instruction");
            _qualifiedSchemeTable = Qualify("equip_scheme");

            _qualifiedInfoPhotoLinkTable = Qualify("equip_info_photo");
            _qualifiedInfoInstructionLinkTable = Qualify("equip_info_instruction");
            _qualifiedInfoSchemeLinkTable = Qualify("equip_info_scheme");

            _qualifiedInfoDocumentViewTable = Qualify("equip_info_pdf_view");
            _qualifiedFavoriteTable = Qualify("equip_favorite");

            _qualifiedSupplierTable = Qualify("equip_supplier");
            _qualifiedOrderTable = Qualify("equip_order");

            _qualifiedNoteTable = Qualify("equip_note");
        }

        public async Task EnsureTableAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var sqlInfo = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedInfoTable}
                (
                    equip_name        text PRIMARY KEY,
                    product_code      text NULL,
                    supplier          text NULL,
                    description       text NULL,
                    updated_at        timestamp NOT NULL DEFAULT now()
                );";

            var sqlPhoto = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedPhotoTable}
                (
                    id                bigserial PRIMARY KEY,
                    equip_type_group  text NOT NULL,
                    file_name         text NOT NULL,
                    display_name      text NOT NULL,
                    file_hash         text NOT NULL,
                    file_data         bytea NOT NULL,
                    updated_at        timestamp NOT NULL DEFAULT now(),

                    CONSTRAINT uq_equip_photo_type_hash
                        UNIQUE (equip_type_group, file_hash)
                );";

            var sqlInstruction = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedInstructionTable}
                (
                    id                bigserial PRIMARY KEY,
                    equip_type_group  text NOT NULL,
                    file_name         text NOT NULL,
                    display_name      text NOT NULL,
                    file_hash         text NOT NULL,
                    file_data         bytea NOT NULL,
                    updated_at        timestamp NOT NULL DEFAULT now(),

                    CONSTRAINT uq_equip_instruction_type_hash
                        UNIQUE (equip_type_group, file_hash)
                );";

            var sqlScheme = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedSchemeTable}
                (
                    id                bigserial PRIMARY KEY,
                    equip_type_group  text NOT NULL,
                    file_name         text NOT NULL,
                    display_name      text NOT NULL,
                    file_hash         text NOT NULL,
                    file_data         bytea NOT NULL,
                    updated_at        timestamp NOT NULL DEFAULT now(),

                    CONSTRAINT uq_equip_scheme_type_hash
                        UNIQUE (equip_type_group, file_hash)
                );";

            var sqlInfoPhotoLink = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedInfoPhotoLinkTable}
                (
                    equip_name   text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                    photo_id     bigint NOT NULL REFERENCES {_qualifiedPhotoTable}(id) ON DELETE CASCADE,
                    sort_order   integer NOT NULL DEFAULT 0,

                    PRIMARY KEY (equip_name, photo_id)
                );";

            var sqlInfoInstructionLink = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedInfoInstructionLinkTable}
                (
                    equip_name      text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                    instruction_id  bigint NOT NULL REFERENCES {_qualifiedInstructionTable}(id) ON DELETE CASCADE,
                    sort_order      integer NOT NULL DEFAULT 0,

                    PRIMARY KEY (equip_name, instruction_id)
                );";

            var sqlInfoSchemeLink = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedInfoSchemeLinkTable}
                (
                    equip_name   text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                    scheme_id    bigint NOT NULL REFERENCES {_qualifiedSchemeTable}(id) ON DELETE CASCADE,
                    sort_order   integer NOT NULL DEFAULT 0,

                    PRIMARY KEY (equip_name, scheme_id)
                );";

            var sqlInfoPdfView = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedInfoDocumentViewTable}
                (
                    equip_name       text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                    info_page_kind   text NOT NULL,
                    file_id          bigint NOT NULL,
                    file_name        text NOT NULL,
                    page_number      integer NOT NULL,
                    zoom_factor      double precision NOT NULL,
                    anchor_x         double precision NOT NULL,
                    anchor_y         double precision NOT NULL,
                    updated_at       timestamp NOT NULL DEFAULT now(),

                    PRIMARY KEY (equip_name, info_page_kind, file_id)
                );";

            var sqlFavorite = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedFavoriteTable}
                (
                    device_name   text NOT NULL,
                    equip_name    text NOT NULL,
                    updated_at    timestamp NOT NULL DEFAULT now(),

                    PRIMARY KEY (device_name, equip_name)
                );";

            var sqlSupplier = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedSupplierTable}
                (
                    id              bigserial PRIMARY KEY,
                    name            text NOT NULL UNIQUE,
                    logo_file_name  text NULL,
                    logo_file_hash  text NULL,
                    logo_data       bytea NULL,
                    updated_at      timestamp NOT NULL DEFAULT now()
                );";

            var sqlOrder = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedOrderTable}
                (
                    id              bigserial PRIMARY KEY,
                    type            text NULL,
                    product_code    text NOT NULL UNIQUE,
                    supplier_id     bigint NULL REFERENCES {_qualifiedSupplierTable}(id) ON DELETE SET NULL,
                    description     text NULL,
                    source          text NULL,
                    image           text NULL,
                    updated_at      timestamp NOT NULL DEFAULT now()
                );";

            var sqlNote = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedNoteTable}
                (
                    id          bigserial PRIMARY KEY,
                    equip_name  text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                    note_text   text NOT NULL,
                    created_by  text NOT NULL,
                    created_at  timestamp NOT NULL DEFAULT now(),
                    updated_by  text NULL,
                    updated_at  timestamp NULL
                );";

            await db.Database.ExecuteSqlRawAsync(sqlInfo, ct);
            await db.Database.ExecuteSqlRawAsync(sqlPhoto, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInstruction, ct);
            await db.Database.ExecuteSqlRawAsync(sqlScheme, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoPhotoLink, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoInstructionLink, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoSchemeLink, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoPdfView, ct);
            await db.Database.ExecuteSqlRawAsync(sqlFavorite, ct);
            await db.Database.ExecuteSqlRawAsync(sqlSupplier, ct);
            await db.Database.ExecuteSqlRawAsync(sqlOrder, ct);
            await db.Database.ExecuteSqlRawAsync(sqlNote, ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_photo_type
            ON {_qualifiedPhotoTable} (equip_type_group, display_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_instruction_type
            ON {_qualifiedInstructionTable} (equip_type_group, display_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_scheme_type
            ON {_qualifiedSchemeTable} (equip_type_group, display_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_photo_type_lower_file_name
            ON {_qualifiedPhotoTable} (equip_type_group, lower(file_name));", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_instruction_type_lower_file_name
            ON {_qualifiedInstructionTable} (equip_type_group, lower(file_name));", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_scheme_type_lower_file_name
            ON {_qualifiedSchemeTable} (equip_type_group, lower(file_name));", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_pdf_view_lookup
            ON {_qualifiedInfoDocumentViewTable} (equip_name, info_page_kind, file_id);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_favorite_equip
            ON {_qualifiedFavoriteTable} (equip_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_favorite_device
            ON {_qualifiedFavoriteTable} (device_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_supplier_name
            ON {_qualifiedSupplierTable} (name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_order_product_code
            ON {_qualifiedOrderTable} (product_code);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_order_supplier_id
            ON {_qualifiedOrderTable} (supplier_id);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_equip_note_equip_created
            ON {_qualifiedNoteTable} (equip_name, created_at DESC, id DESC);", ct);
        }

        public async Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return EquipmentInfoDto.CreateEmpty("");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var model = EquipmentInfoDto.CreateEmpty(equipName);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT
                        equip_name,
                        product_code,
                        supplier,
                        description,
                        updated_at
                    FROM {_qualifiedInfoTable}
                    WHERE equip_name = @equip_name
                    LIMIT 1;";

                AddParameter(cmd, "@equip_name", equipName);

                using var reader = await cmd.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    model.EquipName = reader.IsDBNull(0) ? equipName : reader.GetString(0);
                    model.ProductCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                    model.Supplier = reader.IsDBNull(2) ? null : reader.GetString(2);
                    model.Description = reader.IsDBNull(3) ? null : reader.GetString(3);
                    model.UpdatedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTime>(4);

                    model.IsFavorite = false;
                }
            }

            model.SupplierLogoCachePath = await ResolveSupplierLogoCachePathAsync(
                conn,
                model.ProductCode,
                model.Supplier,
                ct);

            await LoadLinkedFilesAsync(conn, _qualifiedInfoPhotoLinkTable, _qualifiedPhotoTable, "photo_id", model.Photos, equipName, ct);
            await LoadLinkedFilesAsync(conn, _qualifiedInfoInstructionLinkTable, _qualifiedInstructionTable, "instruction_id", model.Instructions, equipName, ct);
            await LoadLinkedFilesAsync(conn, _qualifiedInfoSchemeLinkTable, _qualifiedSchemeTable, "scheme_id", model.Schemes, equipName, ct);

            var notes = await GetNotesAsync(equipName, ct);
            foreach (var note in notes)
                model.Notes.Add(note);

            return model;
        }

        public async Task SaveAsync(EquipmentInfoDto model, CancellationToken ct = default)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                throw new InvalidOperationException("EquipName is empty.");

            NormalizeSortOrders(model.Photos, equipName);
            NormalizeSortOrders(model.Instructions, equipName);
            NormalizeSortOrders(model.Schemes, equipName);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $@"
                INSERT INTO {_qualifiedInfoTable}
                (
                    equip_name,
                    product_code,
                    supplier,
                    description,
                    updated_at
                )
                VALUES
                (
                    @equip_name,
                    @product_code,
                    @supplier,
                    @description,
                    now()
                )
                ON CONFLICT (equip_name)
                DO UPDATE SET
                    product_code = EXCLUDED.product_code,
                    supplier = EXCLUDED.supplier,
                    description = EXCLUDED.description,
                    updated_at = now();";

                    AddParameter(cmd, "@equip_name", equipName);
                    AddParameter(cmd, "@product_code", model.ProductCode);
                    AddParameter(cmd, "@supplier", model.Supplier);
                    AddParameter(cmd, "@description", model.Description);

                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await ReplaceLinksAsync(conn, tx, _qualifiedInfoPhotoLinkTable, "photo_id", equipName, model.Photos, ct);
                await ReplaceLinksAsync(conn, tx, _qualifiedInfoInstructionLinkTable, "instruction_id", equipName, model.Instructions, ct);
                await ReplaceLinksAsync(conn, tx, _qualifiedInfoSchemeLinkTable, "scheme_id", equipName, model.Schemes, ct);

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task<IReadOnlyList<EquipmentInfoFileDto>> GetLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, CancellationToken ct = default)
        {
            equipTypeGroupKey = (equipTypeGroupKey ?? "").Trim();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    updated_at
                                FROM {table}
                                WHERE equip_type_group = @equip_type_group
                                ORDER BY display_name, file_name;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var result = new List<EquipmentInfoFileDto>();
            while (await reader.ReadAsync(ct))
            {
                result.Add(new EquipmentInfoFileDto
                {
                    Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    UpdatedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTime>(5),
                    
                    FileData = null // ВАЖНО: library list не тянет file_data
                });
            }

            return result;
        }

        public async Task<EquipInfoLibraryAddResult> AddFilesToLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, IEnumerable<string> filePaths, CancellationToken ct = default)
        {
            equipTypeGroupKey = (equipTypeGroupKey ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                throw new InvalidOperationException("Equipment type group is empty.");

            var result = new EquipInfoLibraryAddResult();

            var normalizedPaths = (filePaths ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedPaths.Count == 0)
                return result;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                foreach (var path in normalizedPaths)
                {
                    if (!File.Exists(path))
                        continue;

                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    if (bytes.Length == 0)
                        continue;

                    var hash = ComputeFileHash(bytes);

                    var existing = await FindLibraryItemByTypeAndHashAsync(
                        conn, tx, table, equipTypeGroupKey, hash, ct);

                    if (existing != null)
                    {
                        if (result.ResolvedAssets.All(x => x.Id != existing.Id))
                            result.ResolvedAssets.Add(existing);

                        result.ExistingInLibraryFileNames.Add(Path.GetFileName(path));
                        continue;
                    }

                    var inserted = await InsertLibraryItemAsync(
                        conn,
                        tx,
                        table,
                        equipTypeGroupKey,
                        Path.GetFileName(path),
                        Path.GetFileName(path),
                        hash,
                        bytes,
                        ct);

                    result.ResolvedAssets.Add(inserted);
                    result.AddedToLibraryFileNames.Add(inserted.FileName);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            return result;
        }

        private string GetLibraryTable(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _qualifiedPhotoTable,
                InfoFileKind.Instruction => _qualifiedInstructionTable,
                InfoFileKind.Scheme => _qualifiedSchemeTable,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private static async Task LoadLinkedFilesAsync(DbConnection conn, string qualifiedLinkTable, string qualifiedLibraryTable, string linkIdColumn, ObservableCollection<EquipmentInfoFileDto> target, string equipName, CancellationToken ct)
        {
            target.Clear();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    lib.id,
                                    lib.file_name,
                                    lib.display_name,
                                    lib.file_hash,
                                    lib.file_data,
                                    link.sort_order,
                                    lib.updated_at,
                                    lib.equip_type_group
                                FROM {qualifiedLinkTable} link
                                INNER JOIN {qualifiedLibraryTable} lib
                                    ON lib.id = link.{linkIdColumn}
                                WHERE link.equip_name = @equip_name
                                ORDER BY link.sort_order, lib.display_name, lib.file_name;";

            AddParameter(cmd, "@equip_name", equipName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                target.Add(new EquipmentInfoFileDto
                {
                    Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    EquipName = equipName,
                    FileName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    DisplayName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    FileHash = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    FileData = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
                    SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6),
                    EquipTypeGroupKey = reader.IsDBNull(7) ? "" : reader.GetString(7)
                });
            }
        }

        private static async Task ReplaceLinksAsync(DbConnection conn, DbTransaction tx, string qualifiedLinkTable, string linkIdColumn, string equipName, IEnumerable<EquipmentInfoFileDto> files, CancellationToken ct)
        {
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $@"DELETE FROM {qualifiedLinkTable} WHERE equip_name = @equip_name;";
                AddParameter(deleteCmd, "@equip_name", equipName);
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            var usedIds = new HashSet<long>();
            var sortOrder = 0;

            foreach (var file in files ?? Enumerable.Empty<EquipmentInfoFileDto>())
            {
                if (file == null || file.Id <= 0)
                    continue;

                if (!usedIds.Add(file.Id))
                    continue;

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = $@"
INSERT INTO {qualifiedLinkTable}
(
    equip_name,
    {linkIdColumn},
    sort_order
)
VALUES
(
    @equip_name,
    @file_id,
    @sort_order
);";

                AddParameter(insertCmd, "@equip_name", equipName);
                AddParameter(insertCmd, "@file_id", file.Id);
                AddParameter(insertCmd, "@sort_order", sortOrder++);

                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static async Task<EquipmentInfoFileDto> InsertLibraryItemAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, string equipTypeGroupKey, string fileName, string displayName, string fileHash, byte[] fileData, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                                INSERT INTO {qualifiedTable}
                                (
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at
                                )
                                VALUES
                                (
                                    @equip_type_group,
                                    @file_name,
                                    @display_name,
                                    @file_hash,
                                    @file_data,
                                    now()
                                )
                                RETURNING
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);
            AddParameter(cmd, "@file_name", fileName);
            AddParameter(cmd, "@display_name", displayName);
            AddParameter(cmd, "@file_hash", fileHash);
            AddParameter(cmd, "@file_data", fileData);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            return ReadLibraryItem(reader);
        }

        private static EquipmentInfoFileDto ReadLibraryItem(DbDataReader reader)
        {
            return new EquipmentInfoFileDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FileData = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6)
            };
        }

        private static void NormalizeSortOrders(IEnumerable<EquipmentInfoFileDto> files, string equipName)
        {
            if (files == null)
                return;

            var index = 0;
            foreach (var item in files)
            {
                if (item == null)
                    continue;

                item.EquipName = equipName;
                item.SortOrder = index++;
            }
        }

        private static string ComputeFileHash(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
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

        private static async Task<EquipmentInfoFileDto?> FindLibraryItemByTypeAndHashAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, string equipTypeGroupKey, string hash, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                                SELECT
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at
                                FROM {qualifiedTable}
                                WHERE equip_type_group = @equip_type_group
                                  AND file_hash = @file_hash
                                LIMIT 1;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);
            AddParameter(cmd, "@file_hash", hash);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return ReadLibraryItem(reader);
        }

        public async Task<EquipmentInfoFileDto?> GetLibraryFileByIdAsync(InfoFileKind kind, long id, CancellationToken ct = default)
        {
            if (id <= 0)
                return null;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at
                                FROM {table}
                                WHERE id = @id
                                LIMIT 1;";

            AddParameter(cmd, "@id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new EquipmentInfoFileDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FileData = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6)
            };
        }

        public async Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(string equipName, InfoPageKind pageKind, long fileId, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName) || fileId <= 0)
                return null;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
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
                                FROM {_qualifiedInfoDocumentViewTable}
                                WHERE equip_name = @equip_name
                                  AND info_page_kind = @info_page_kind
                                  AND file_id = @file_id
                                LIMIT 1;";

            AddParameter(cmd, "@equip_name", equipName);
            AddParameter(cmd, "@info_page_kind", pageKind.ToString());
            AddParameter(cmd, "@file_id", fileId);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new EquipmentInfoDocumentViewStateDto
            {
                EquipName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                InfoPageKind = Enum.TryParse<InfoPageKind>(reader.IsDBNull(1) ? "" : reader.GetString(1), out var parsedKind)
                    ? parsedKind
                    : pageKind,
                FileId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                FileName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                PageNumber = reader.IsDBNull(4) ? 1 : reader.GetInt32(4),
                ZoomFactor = reader.IsDBNull(5) ? 1.0 : reader.GetDouble(5),
                AnchorX = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6),
                AnchorY = reader.IsDBNull(7) ? 0.0 : reader.GetDouble(7),
                UpdatedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTime>(8)
            };
        }

        public async Task SaveDocumentViewStateAsync(EquipmentInfoDocumentViewStateDto model, CancellationToken ct = default)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                throw new InvalidOperationException("EquipName is empty.");

            if (model.FileId <= 0)
                throw new InvalidOperationException("FileId must be greater than 0.");

            if (model.PageNumber <= 0)
                throw new InvalidOperationException("PageNumber must be greater than 0.");

            if (model.ZoomFactor <= 0)
                throw new InvalidOperationException("ZoomFactor must be greater than 0.");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            // ВАЖНО:
            // saved PDF position ссылается по FK на info table,
            // поэтому для нового equipment сначала гарантируем базовую строку.
            await EnsureInfoRowExistsAsync(conn, equipName, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                INSERT INTO {_qualifiedInfoDocumentViewTable}
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
                                    file_name   = EXCLUDED.file_name,
                                    page_number = EXCLUDED.page_number,
                                    zoom_factor = EXCLUDED.zoom_factor,
                                    anchor_x    = EXCLUDED.anchor_x,
                                    anchor_y    = EXCLUDED.anchor_y,
                                    updated_at  = now();";

            AddParameter(cmd, "@equip_name", equipName);
            AddParameter(cmd, "@info_page_kind", model.InfoPageKind.ToString());
            AddParameter(cmd, "@file_id", model.FileId);
            AddParameter(cmd, "@file_name", model.FileName);
            AddParameter(cmd, "@page_number", model.PageNumber);
            AddParameter(cmd, "@zoom_factor", model.ZoomFactor);
            AddParameter(cmd, "@anchor_x", model.AnchorX);
            AddParameter(cmd, "@anchor_y", model.AnchorY);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Гарантирует наличие базовой строки equipment в info table.
        /// Нужно для сущностей, которые ссылаются на equip_name по FK
        /// (например, saved PDF view position), даже если карточка ещё ни разу не сохранялась вручную.
        /// </summary>
        private async Task EnsureInfoRowExistsAsync(DbConnection conn, string equipName, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                    INSERT INTO {_qualifiedInfoTable}
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
                    DO NOTHING;";

            AddParameter(cmd, "@equip_name", equipName);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<bool> DeleteLibraryFileAsync(InfoFileKind kind, long id, CancellationToken ct = default)
        {
            if (id <= 0)
                return false;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                DELETE FROM {table}
                                WHERE id = @id;";

            AddParameter(cmd, "@id", id);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }

        public async Task<IReadOnlyCollection<string>> GetFavoriteEquipNamesAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                    SELECT equip_name
                    FROM {_qualifiedFavoriteTable}
                    WHERE device_name = @device_name
                    ORDER BY equip_name;";

            AddParameter(cmd, "@device_name", _appRuntime.DeviceName);

            var result = new List<string>();

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var equipName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                equipName = (equipName ?? "").Trim();

                if (!string.IsNullOrWhiteSpace(equipName))
                    result.Add(equipName);
            }

            return result;
        }

        public Task<IReadOnlyCollection<string>> GetEquipNamesWithLinkedPhotosAsync(CancellationToken ct = default)
        {
            return GetDistinctEquipNamesFromLinkTableAsync(_qualifiedInfoPhotoLinkTable, ct);
        }

        public Task<IReadOnlyCollection<string>> GetEquipNamesWithLinkedInstructionsAsync(CancellationToken ct = default)
        {
            return GetDistinctEquipNamesFromLinkTableAsync(_qualifiedInfoInstructionLinkTable, ct);
        }

        public Task<IReadOnlyCollection<string>> GetEquipNamesWithLinkedSchemesAsync(CancellationToken ct = default)
        {
            return GetDistinctEquipNamesFromLinkTableAsync(_qualifiedInfoSchemeLinkTable, ct);
        }

        public async Task<IReadOnlyCollection<string>> GetEquipNamesWithNotesAsync(CancellationToken ct = default)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT DISTINCT equip_name
                FROM {_qualifiedNoteTable}
                WHERE note_text IS NOT NULL
                  AND btrim(note_text) <> '';";

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var equipName = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();

                if (!string.IsNullOrWhiteSpace(equipName))
                    result.Add(equipName);
            }

            return result;
        }

        private async Task<IReadOnlyCollection<string>> GetDistinctEquipNamesFromLinkTableAsync(string qualifiedLinkTable, CancellationToken ct = default)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
        SELECT DISTINCT equip_name
        FROM {qualifiedLinkTable};";

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var equipName = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();

                if (!string.IsNullOrWhiteSpace(equipName))
                    result.Add(equipName);
            }

            return result;
        }

        public async Task SetFavoriteAsync(string equipName, bool isFavorite, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                throw new InvalidOperationException("EquipName is empty.");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            if (isFavorite)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
            INSERT INTO {_qualifiedFavoriteTable}
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
                updated_at = now();";

                AddParameter(cmd, "@device_name", _appRuntime.DeviceName);
                AddParameter(cmd, "@equip_name", equipName);

                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
            DELETE FROM {_qualifiedFavoriteTable}
            WHERE device_name = @device_name
              AND equip_name = @equip_name;";

                AddParameter(cmd, "@device_name", _appRuntime.DeviceName);
                AddParameter(cmd, "@equip_name", equipName);

                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        private string Qualify(string tableName) => $"{QuoteIdentifier(SchemaName)}.{QuoteIdentifier(tableName)}";

        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        public async Task EnsureDatabaseAndTablesAsync(CancellationToken ct = default)
        {
            await EnsureDatabaseExistsAsync(ct);
            await EnsureTableAsync(ct);
        }

        private async Task EnsureDatabaseExistsAsync(CancellationToken ct)
        {
            var cs = _config.GetConnectionString("PostgresInfo");
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("ConnectionStrings:PostgresInfo is not configured.");

            var builder = new NpgsqlConnectionStringBuilder(cs);
            var targetDb = (builder.Database ?? "").Trim();

            if (string.IsNullOrWhiteSpace(targetDb))
                throw new InvalidOperationException("PostgresInfo database name is empty.");

            // Подключаемся к служебной БД postgres
            builder.Database = "postgres";

            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db;";
                checkCmd.Parameters.AddWithValue("@db", targetDb);

                var exists = await checkCmd.ExecuteScalarAsync(ct);
                if (exists != null)
                    return;
            }

            // CREATE DATABASE нельзя выполнять внутри транзакции
            try
            {
                await using var createCmd = conn.CreateCommand();
                createCmd.CommandText = $"CREATE DATABASE {QuoteIdentifier(targetDb)};";
                await createCmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P04") // duplicate_database
            {
                // Кто-то успел создать БД параллельно.
            }

        }

        public async Task<InfoPhotoImportDbResult> ImportPhotoForEquipmentAsync(string equipName, string equipTypeGroup, string filePath, CancellationToken cancellationToken = default)
        {
            var bulk = await ImportPhotoForEquipmentsAsync(
                equipTypeGroup,
                filePath,
                new[] { equipName },
                cancellationToken);

            var status =
                bulk.AddedToDb ? InfoPhotoImportDbStatus.AddedToDbAndLinked :
                bulk.UpdatedInDb ? InfoPhotoImportDbStatus.LinkedExisting :
                bulk.LinksCreated > 0 ? InfoPhotoImportDbStatus.LinkedExisting :
                InfoPhotoImportDbStatus.AlreadyLinked;

            return new InfoPhotoImportDbResult
            {
                PhotoId = bulk.PhotoId,
                Status = status
            };
        }

        public async Task<InfoDocumentImportDbResult> ImportDocumentForEquipmentAsync(InfoFileKind kind, string equipName, string equipTypeGroup, string filePath, CancellationToken cancellationToken = default)
        {
            var bulk = await ImportDocumentForEquipmentsAsync(
                kind,
                equipTypeGroup,
                filePath,
                new[] { equipName },
                cancellationToken);

            InfoDocumentImportDbStatus status;

            if (bulk.AddedToDb)
            {
                status = InfoDocumentImportDbStatus.AddedToDbAndLinked;
            }
            else if (bulk.UpdatedInDb)
            {
                status = InfoDocumentImportDbStatus.UpdatedExistingAndLinked;
            }
            else if (bulk.LinksCreated > 0)
            {
                status = InfoDocumentImportDbStatus.LinkedExisting;
            }
            else
            {
                status = InfoDocumentImportDbStatus.AlreadyLinked;
            }

            return new InfoDocumentImportDbResult
            {
                DocumentId = bulk.DocumentId,
                Status = status
            };
        }

        public async Task<InfoDocumentBulkImportDbResult> ImportDocumentForEquipmentsAsync(InfoFileKind kind, string equipTypeGroup, string filePath, IEnumerable<string> equipNames, CancellationToken cancellationToken = default)
        {
            if (kind != InfoFileKind.Instruction && kind != InfoFileKind.Scheme)
                throw new InvalidOperationException("Only Instruction and Scheme documents can be imported with this method.");

            return await ImportLibraryFileForEquipmentsAsync(
                kind,
                equipTypeGroup,
                filePath,
                equipNames,
                cancellationToken);
        }

        private async Task EnsureInfoRowsAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<string> equipNames, CancellationToken ct)
        {
            var cleanEquipNames = (equipNames ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (cleanEquipNames.Length == 0)
                return;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                    INSERT INTO {_qualifiedInfoTable}
                    (
                        equip_name,
                        updated_at
                    )
                    SELECT
                        e.equip_name,
                        now()
                    FROM unnest(@equip_names::text[]) AS e(equip_name)
                    ON CONFLICT (equip_name)
                    DO NOTHING;";

            AddParameter(cmd, "@equip_names", cleanEquipNames);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task EnsureInfoRowExistsAsync(DbConnection conn, DbTransaction tx, string equipName, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            cmd.CommandText = $@"
                INSERT INTO {_qualifiedInfoTable}
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
                DO NOTHING;";

            AddParameter(cmd, "@equip_name", equipName);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<EquipmentInfoFileDto?> FindLibraryItemByTypeAndFileNameAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, string equipTypeGroupKey, string fileName, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
        SELECT
            id,
            equip_type_group,
            file_name,
            display_name,
            file_hash,
            NULL::bytea AS file_data,
            updated_at
        FROM {qualifiedTable}
        WHERE equip_type_group = @equip_type_group
          AND lower(file_name) = lower(@file_name)
        ORDER BY updated_at DESC NULLS LAST, id DESC
        LIMIT 1;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);
            AddParameter(cmd, "@file_name", fileName);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return null;

            return ReadLibraryFile(reader, includeData: false);
        }

        private static async Task<EquipmentInfoFileDto> InsertLibraryItemWithUpdatedAtAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, string equipTypeGroupKey, string fileName, string displayName, string fileHash, byte[] fileData, DateTime updatedAt, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
        INSERT INTO {qualifiedTable}
        (
            equip_type_group,
            file_name,
            display_name,
            file_hash,
            file_data,
            updated_at
        )
        VALUES
        (
            @equip_type_group,
            @file_name,
            @display_name,
            @file_hash,
            @file_data,
            @updated_at
        )
        RETURNING
            id,
            equip_type_group,
            file_name,
            display_name,
            file_hash,
            NULL::bytea AS file_data,
            updated_at;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);
            AddParameter(cmd, "@file_name", fileName);
            AddParameter(cmd, "@display_name", displayName);
            AddParameter(cmd, "@file_hash", fileHash);
            AddParameter(cmd, "@file_data", fileData);
            AddParameter(cmd, "@updated_at", updatedAt);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Inserted document was not returned by DB.");

            return ReadLibraryFile(reader, includeData: false);
        }

        private static async Task UpdateLibraryItemDataAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, long id, string fileName, string displayName, string fileHash, byte[] fileData, DateTime updatedAt, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
        UPDATE {qualifiedTable}
        SET
            file_name = @file_name,
            display_name = @display_name,
            file_hash = @file_hash,
            file_data = @file_data,
            updated_at = @updated_at
        WHERE id = @id;";

            AddParameter(cmd, "@id", id);
            AddParameter(cmd, "@file_name", fileName);
            AddParameter(cmd, "@display_name", displayName);
            AddParameter(cmd, "@file_hash", fileHash);
            AddParameter(cmd, "@file_data", fileData);
            AddParameter(cmd, "@updated_at", updatedAt);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private sealed class BulkDocumentLinkResult
        {
            public List<string> CreatedEquipNames { get; } = new();
        }

        private static async Task<BulkDocumentLinkResult> EnsureDocumentLinksAsync(DbConnection conn, DbTransaction tx, string qualifiedLinkTable, string linkIdColumn, IReadOnlyCollection<string> equipNames, long documentId, CancellationToken ct)
        {
            var result = new BulkDocumentLinkResult();

            var cleanEquipNames = (equipNames ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (cleanEquipNames.Length == 0)
                return result;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            cmd.CommandText = $@"
        WITH input AS
        (
            SELECT DISTINCT btrim(e.equip_name) AS equip_name
            FROM unnest(@equip_names::text[]) AS e(equip_name)
            WHERE btrim(e.equip_name) <> ''
        ),
        to_insert AS
        (
            SELECT
                input.equip_name,
                COALESCE(
                    (
                        SELECT MAX(link.sort_order)
                        FROM {qualifiedLinkTable} link
                        WHERE link.equip_name = input.equip_name
                    ),
                    -1
                ) + 1 AS next_sort_order
            FROM input
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM {qualifiedLinkTable} existing_link
                WHERE existing_link.equip_name = input.equip_name
                  AND existing_link.{linkIdColumn} = @document_id
            )
        )
        INSERT INTO {qualifiedLinkTable}
        (
            equip_name,
            {linkIdColumn},
            sort_order
        )
        SELECT
            equip_name,
            @document_id,
            next_sort_order
        FROM to_insert
        ON CONFLICT DO NOTHING
        RETURNING equip_name;";

            AddParameter(cmd, "@equip_names", cleanEquipNames);
            AddParameter(cmd, "@document_id", documentId);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var equipName = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();

                if (!string.IsNullOrWhiteSpace(equipName))
                    result.CreatedEquipNames.Add(equipName);
            }

            return result;
        }

        private static EquipmentInfoFileDto ReadLibraryFile(DbDataReader reader, bool includeData)
        {
            return new EquipmentInfoFileDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FileData = includeData && !reader.IsDBNull(5)
                    ? (byte[])reader.GetValue(5)
                    : null,
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6)
            };
        }

        public async Task<int> UpsertSuppliersAsync(IEnumerable<InstructionSupplierRow> suppliers, CancellationToken ct = default)
        {
            var clean = (suppliers ?? Enumerable.Empty<InstructionSupplierRow>())
                .Where(x => x != null)
                .Where(x => !string.IsNullOrWhiteSpace(x.Supplier))
                .GroupBy(x => x.Supplier.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

            if (clean.Count == 0)
                return 0;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                var count = 0;

                foreach (var item in clean)
                {
                    var supplierName = item.Supplier.Trim();

                    string? logoFileName = null;
                    string? logoHash = null;
                    byte[]? logoData = null;

                    if (!string.IsNullOrWhiteSpace(item.SupplierLogoPath) &&
                        File.Exists(item.SupplierLogoPath))
                    {
                        logoFileName = Path.GetFileName(item.SupplierLogoPath);
                        logoData = await File.ReadAllBytesAsync(item.SupplierLogoPath, ct);
                        logoHash = logoData.Length > 0 ? ComputeFileHash(logoData) : null;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $@"
                INSERT INTO {_qualifiedSupplierTable}
                (
                    name,
                    logo_file_name,
                    logo_file_hash,
                    logo_data,
                    updated_at
                )
                VALUES
                (
                    @name,
                    @logo_file_name,
                    @logo_file_hash,
                    @logo_data,
                    now()
                )
                ON CONFLICT (name)
                DO UPDATE SET
                    logo_file_name = COALESCE(EXCLUDED.logo_file_name, {_qualifiedSupplierTable}.logo_file_name),
                    logo_file_hash = COALESCE(EXCLUDED.logo_file_hash, {_qualifiedSupplierTable}.logo_file_hash),
                    logo_data = COALESCE(EXCLUDED.logo_data, {_qualifiedSupplierTable}.logo_data),
                    updated_at = CASE
                        WHEN EXCLUDED.logo_file_hash IS NOT NULL
                             AND COALESCE(EXCLUDED.logo_file_hash, '') IS DISTINCT FROM COALESCE({_qualifiedSupplierTable}.logo_file_hash, '')
                            THEN now()
                        ELSE {_qualifiedSupplierTable}.updated_at
                    END;";

                    AddParameter(cmd, "@name", supplierName);
                    AddParameter(cmd, "@logo_file_name", logoFileName);
                    AddParameter(cmd, "@logo_file_hash", logoHash);
                    AddParameter(cmd, "@logo_data", logoData);

                    await cmd.ExecuteNonQueryAsync(ct);
                    count++;
                }

                await tx.CommitAsync(ct);
                return count;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task<int> UpsertOrdersAsync(IEnumerable<InstructionOrderRow> orders, CancellationToken ct = default)
        {
            var clean = (orders ?? Enumerable.Empty<InstructionOrderRow>())
                .Where(x => x != null)
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => x.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

            if (clean.Count == 0)
                return 0;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                var count = 0;

                foreach (var item in clean)
                {
                    long? supplierId = null;
                    var supplierName = (item.Supplier ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(supplierName))
                    {
                        using var supplierCmd = conn.CreateCommand();
                        supplierCmd.Transaction = tx;
                        supplierCmd.CommandText = $@"
                    INSERT INTO {_qualifiedSupplierTable}
                    (
                        name,
                        updated_at
                    )
                    VALUES
                    (
                        @name,
                        now()
                    )
                    ON CONFLICT (name)
                    DO UPDATE SET
                        updated_at = {_qualifiedSupplierTable}.updated_at
                    RETURNING id;";

                        AddParameter(supplierCmd, "@name", supplierName);

                        var id = await supplierCmd.ExecuteScalarAsync(ct);
                        supplierId = id == null || id == DBNull.Value ? null : Convert.ToInt64(id);
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $@"
                INSERT INTO {_qualifiedOrderTable}
                (
                    type,
                    product_code,
                    supplier_id,
                    description,
                    source,
                    image,
                    updated_at
                )
                VALUES
                (
                    @type,
                    @product_code,
                    @supplier_id,
                    @description,
                    @source,
                    @image,
                    now()
                )
                ON CONFLICT (product_code)
                DO UPDATE SET
                    type = EXCLUDED.type,
                    supplier_id = EXCLUDED.supplier_id,
                    description = EXCLUDED.description,
                    source = EXCLUDED.source,
                    image = EXCLUDED.image,
                    updated_at = now();";

                    AddParameter(cmd, "@type", item.Type);
                    AddParameter(cmd, "@product_code", item.ProductCode.Trim());
                    AddParameter(cmd, "@supplier_id", supplierId);
                    AddParameter(cmd, "@description", item.Description);
                    AddParameter(cmd, "@source", item.Source);
                    AddParameter(cmd, "@image", item.Image);

                    await cmd.ExecuteNonQueryAsync(ct);
                    count++;
                }

                await tx.CommitAsync(ct);
                return count;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task<IReadOnlyDictionary<string, InfoOrderCatalogDto>> GetOrdersByProductCodesAsync(IEnumerable<string> productCodes, CancellationToken ct = default)
        {
            var codes = (productCodes ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = new Dictionary<string, InfoOrderCatalogDto>(StringComparer.OrdinalIgnoreCase);

            if (codes.Length == 0)
                return result;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
        SELECT
            o.type,
            o.product_code,
            COALESCE(s.name, '') AS supplier,
            COALESCE(o.description, '') AS description,
            COALESCE(o.source, '') AS source,
            COALESCE(o.image, '') AS image
        FROM {_qualifiedOrderTable} o
        LEFT JOIN {_qualifiedSupplierTable} s
            ON s.id = o.supplier_id
        WHERE o.product_code = ANY(@product_codes);";

            AddParameter(cmd, "@product_codes", codes);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var dto = new InfoOrderCatalogDto
                {
                    Type = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    ProductCode = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Supplier = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Source = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Image = reader.IsDBNull(5) ? "" : reader.GetString(5)
                };

                foreach (var item in SplitCsv(dto.Source))
                    dto.Sources.Add(item);

                foreach (var item in SplitCsv(dto.Image))
                    dto.Images.Add(item);

                if (!string.IsNullOrWhiteSpace(dto.ProductCode))
                    result[dto.ProductCode] = dto;
            }

            return result;
        }

        public async Task<IReadOnlyList<InfoProductCodeOptionDto>> GetProductCodeOptionsAsync(string equipTypeGroupKey, CancellationToken ct = default)
        {
            equipTypeGroupKey = (equipTypeGroupKey ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return Array.Empty<InfoProductCodeOptionDto>();

            var result = new List<InfoProductCodeOptionDto>();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT
                        COALESCE(o.type, '') AS type,
                        COALESCE(o.product_code, '') AS product_code,
                        COALESCE(s.name, '') AS supplier,
                        COALESCE(o.description, '') AS description
                    FROM {_qualifiedOrderTable} o
                    LEFT JOIN {_qualifiedSupplierTable} s
                        ON s.id = o.supplier_id
                    WHERE lower(COALESCE(o.type, '')) = lower(@type)
                    ORDER BY
                        lower(COALESCE(o.type, '')),
                        lower(COALESCE(o.product_code, ''));";

                AddParameter(cmd, "@type", equipTypeGroupKey);

                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    var option = new InfoProductCodeOptionDto
                    {
                        Type = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ProductCode = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Supplier = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Description = reader.IsDBNull(3) ? "" : reader.GetString(3)
                    };

                    if (!string.IsNullOrWhiteSpace(option.ProductCode))
                        result.Add(option);
                }
            }

            // ВАЖНО:
            // reader выше уже закрыт, поэтому теперь можно выполнять новые SQL-команды
            // на этом же connection внутри ResolveSupplierLogoCachePathAsync().
            foreach (var option in result)
            {
                option.SupplierLogoCachePath = await ResolveSupplierLogoCachePathAsync(
                    conn,
                    option.ProductCode,
                    option.Supplier,
                    ct);
            }

            return result;
        }

        public async Task<int> ApplyProductCodesToEquipmentInfoAsync(IEnumerable<EquipmentProductCodeLinkDto> links, CancellationToken ct = default)
        {
            var clean = (links ?? Enumerable.Empty<EquipmentProductCodeLinkDto>())
                .Where(x => x != null)
                .Where(x => !string.IsNullOrWhiteSpace(x.EquipName))
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => x.EquipName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToArray();

            if (clean.Length == 0)
                return 0;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
        WITH input AS
        (
            SELECT
                btrim(x.equip_name) AS equip_name,
                btrim(x.product_code) AS product_code
            FROM unnest(
                @equip_names::text[],
                @product_codes::text[]
            ) AS x(equip_name, product_code)
            WHERE btrim(x.equip_name) <> ''
              AND btrim(x.product_code) <> ''
        ),
        resolved AS
        (
            SELECT
                input.equip_name,
                input.product_code,
                COALESCE(s.name, '') AS supplier,
                COALESCE(o.description, '') AS description
            FROM input
            INNER JOIN {_qualifiedOrderTable} o
                ON o.product_code = input.product_code
            LEFT JOIN {_qualifiedSupplierTable} s
                ON s.id = o.supplier_id
        )
        INSERT INTO {_qualifiedInfoTable}
        (
            equip_name,
            product_code,
            supplier,
            description,
            updated_at
        )
        SELECT
            equip_name,
            product_code,
            NULLIF(supplier, ''),
            NULLIF(description, ''),
            now()
        FROM resolved
        ON CONFLICT (equip_name)
        DO UPDATE SET
            product_code = EXCLUDED.product_code,
            supplier = EXCLUDED.supplier,
            description = EXCLUDED.description,
            updated_at = now()
        RETURNING equip_name;";

            AddParameter(cmd, "@equip_names", clean.Select(x => x.EquipName.Trim()).ToArray());
            AddParameter(cmd, "@product_codes", clean.Select(x => x.ProductCode.Trim()).ToArray());

            var updated = 0;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                updated++;

            return updated;
        }

        public async Task<InfoPhotoBulkImportDbResult> ImportPhotoForEquipmentsAsync(string equipTypeGroup, string filePath, IEnumerable<string> equipNames, CancellationToken cancellationToken = default)
        {
            var bulk = await ImportLibraryFileForEquipmentsAsync(
                InfoFileKind.Photo,
                equipTypeGroup,
                filePath,
                equipNames,
                cancellationToken);

            return new InfoPhotoBulkImportDbResult
            {
                PhotoId = bulk.DocumentId,
                AddedToDb = bulk.AddedToDb,
                UpdatedInDb = bulk.UpdatedInDb,
                LinksCreated = bulk.LinksCreated,
                AlreadyLinked = bulk.AlreadyLinked,
                AffectedEquipNames = bulk.AffectedEquipNames
            };
        }

        public async Task<IReadOnlyList<EquipmentInfoNoteDto>> GetNotesAsync(string equipName, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipName))
                return Array.Empty<EquipmentInfoNoteDto>();

            var result = new List<EquipmentInfoNoteDto>();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
        SELECT
            id,
            equip_name,
            note_text,
            created_by,
            created_at,
            updated_by,
            updated_at
        FROM {_qualifiedNoteTable}
        WHERE equip_name = @equip_name
        ORDER BY created_at DESC, id DESC;";

            AddParameter(cmd, "@equip_name", equipName);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var note = ReadNote(reader);
                note.AcceptChanges();
                result.Add(note);
            }

            return result;
        }

        public async Task<IReadOnlyList<EquipmentInfoNoteDto>> SaveNotesAsync(string equipName, IEnumerable<EquipmentInfoNoteDto> notes, string userName, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            userName = (userName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipName))
                return Array.Empty<EquipmentInfoNoteDto>();

            if (string.IsNullOrWhiteSpace(userName))
                userName = "Unknown";

            var dirtyNotes = (notes ?? Enumerable.Empty<EquipmentInfoNoteDto>())
                .Where(x => x != null)
                .Where(x => x.IsNew || x.IsDirty)
                .Where(x => !string.IsNullOrWhiteSpace(x.NoteText))
                .ToList();

            if (dirtyNotes.Count == 0)
                return await GetNotesAsync(equipName, ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                await EnsureInfoRowExistsAsync(conn, equipName, ct);

                foreach (var note in dirtyNotes)
                {
                    if (note.Id <= 0)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = $@"
                    INSERT INTO {_qualifiedNoteTable}
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
                    );";

                        AddParameter(cmd, "@equip_name", equipName);
                        AddParameter(cmd, "@note_text", note.NoteText);
                        AddParameter(cmd, "@created_by", userName);

                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = $@"
                    UPDATE {_qualifiedNoteTable}
                    SET
                        note_text = @note_text,
                        updated_by = @updated_by,
                        updated_at = now()
                    WHERE id = @id
                      AND equip_name = @equip_name;";

                        AddParameter(cmd, "@id", note.Id);
                        AddParameter(cmd, "@equip_name", equipName);
                        AddParameter(cmd, "@note_text", note.NoteText);
                        AddParameter(cmd, "@updated_by", userName);

                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            return await GetNotesAsync(equipName, ct);
        }

        public async Task DeleteNoteAsync(long noteId, CancellationToken ct = default)
        {
            if (noteId <= 0)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                DELETE FROM {_qualifiedNoteTable}
                WHERE id = @id;";

            AddParameter(cmd, "@id", noteId);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task<InfoDocumentBulkImportDbResult> ImportLibraryFileForEquipmentsAsync(InfoFileKind kind, string equipTypeGroup, string filePath, IEnumerable<string> equipNames, CancellationToken cancellationToken)
        {
            equipTypeGroup = (equipTypeGroup ?? "").Trim();

            var cleanEquipNames = (equipNames ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (kind != InfoFileKind.Photo &&
                kind != InfoFileKind.Instruction &&
                kind != InfoFileKind.Scheme)
                throw new InvalidOperationException($"Unsupported file kind: {kind}");

            if (string.IsNullOrWhiteSpace(equipTypeGroup))
                throw new InvalidOperationException("Equipment type group is empty.");

            if (cleanEquipNames.Length == 0)
                throw new InvalidOperationException("Equipment list is empty.");

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("File not found.", filePath);

            var fileName = Path.GetFileName(filePath);
            var displayName = Path.GetFileName(filePath);
            var sourceUpdatedAt = File.GetLastWriteTime(filePath);

            var fileData = await File.ReadAllBytesAsync(filePath, cancellationToken);
            if (fileData.Length == 0)
                throw new InvalidOperationException($"File is empty: {fileName}");

            var fileHash = ComputeFileHash(fileData);

            var table = GetLibraryTable(kind);
            var linkInfo = GetLibraryLinkInfo(kind);

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, cancellationToken);

            await using var tx = await conn.BeginTransactionAsync(cancellationToken);

            try
            {
                await EnsureInfoRowsAsync(conn, tx, cleanEquipNames, cancellationToken);

                long fileId;
                var addedToDb = false;
                var updatedInDb = false;

                var existingByHash = await FindLibraryItemByTypeAndHashAsync(
                    conn,
                    tx,
                    table,
                    equipTypeGroup,
                    fileHash,
                    cancellationToken);

                if (existingByHash != null)
                {
                    fileId = existingByHash.Id;
                }
                else
                {
                    var existingByName = await FindLibraryItemByTypeAndFileNameAsync(
                        conn,
                        tx,
                        table,
                        equipTypeGroup,
                        fileName,
                        cancellationToken);

                    if (existingByName != null)
                    {
                        fileId = existingByName.Id;

                        var shouldUpdate =
                            !existingByName.UpdatedAt.HasValue ||
                            sourceUpdatedAt > existingByName.UpdatedAt.Value;

                        if (shouldUpdate)
                        {
                            await UpdateLibraryItemDataAsync(
                                conn,
                                tx,
                                table,
                                fileId,
                                fileName,
                                displayName,
                                fileHash,
                                fileData,
                                sourceUpdatedAt,
                                cancellationToken);

                            updatedInDb = true;
                        }
                    }
                    else
                    {
                        var inserted = await InsertLibraryItemWithUpdatedAtAsync(
                            conn,
                            tx,
                            table,
                            equipTypeGroup,
                            fileName,
                            displayName,
                            fileHash,
                            fileData,
                            sourceUpdatedAt,
                            cancellationToken);

                        fileId = inserted.Id;
                        addedToDb = true;
                    }
                }

                var linkResult = await EnsureDocumentLinksAsync(
                    conn,
                    tx,
                    linkInfo.LinkTable,
                    linkInfo.LinkIdColumn,
                    cleanEquipNames,
                    fileId,
                    cancellationToken);

                await tx.CommitAsync(cancellationToken);

                return new InfoDocumentBulkImportDbResult
                {
                    DocumentId = fileId,
                    AddedToDb = addedToDb,
                    UpdatedInDb = updatedInDb,
                    LinksCreated = linkResult.CreatedEquipNames.Count,
                    AlreadyLinked = Math.Max(0, cleanEquipNames.Length - linkResult.CreatedEquipNames.Count),
                    AffectedEquipNames = cleanEquipNames
                };
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        private (string LinkTable, string LinkIdColumn) GetLibraryLinkInfo(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => (_qualifiedInfoPhotoLinkTable, "photo_id"),
                InfoFileKind.Instruction => (_qualifiedInfoInstructionLinkTable, "instruction_id"),
                InfoFileKind.Scheme => (_qualifiedInfoSchemeLinkTable, "scheme_id"),
                _ => throw new InvalidOperationException($"Unsupported file kind: {kind}")
            };
        }

        private async Task<string?> ResolveSupplierLogoCachePathAsync(DbConnection conn, string? productCode, string? supplierName, CancellationToken ct)
        {
            productCode = (productCode ?? "").Trim();
            supplierName = (supplierName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(productCode) &&
                string.IsNullOrWhiteSpace(supplierName))
                return null;

            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                SELECT
                    s.logo_file_name,
                    s.logo_file_hash,
                    s.logo_data
                FROM {_qualifiedSupplierTable} s
                LEFT JOIN {_qualifiedOrderTable} o
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
                    s.id
                LIMIT 1;";

            AddParameter(cmd, "@product_code", productCode);
            AddParameter(cmd, "@supplier_name", supplierName);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return null;

            var fileName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var hash = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var data = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);

            if (data == null || data.Length == 0 || string.IsNullOrWhiteSpace(hash))
                return null;

            var cacheFolder = GetSupplierLogoCacheFolder();
            Directory.CreateDirectory(cacheFolder);

            var safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(fileName) ? "supplier_logo.png" : fileName);
            var cachePath = Path.Combine(cacheFolder, $"{hash}_{safeName}");

            if (!File.Exists(cachePath))
                await File.WriteAllBytesAsync(cachePath, data, ct);

            return cachePath;
        }

        private static string GetSupplierLogoCacheFolder()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "TechEquipments", "SupplierLogo");
        }

        private static string MakeSafeFileName(string fileName)
        {
            var invalid = Path.GetInvalidFileNameChars();

            var chars = fileName
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();

            return new string(chars);
        }

        private static IEnumerable<string> SplitCsv(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            foreach (var part in text.Split(','))
            {
                var value = part.Trim().Trim('"', '\'');

                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
        }

        private static EquipmentInfoNoteDto ReadNote(DbDataReader reader)
        {
            return new EquipmentInfoNoteDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                NoteText = reader.IsDBNull(2) ? "" : reader.GetString(2),
                CreatedBy = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CreatedAt = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetFieldValue<DateTime>(4),
                UpdatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6)
            };
        }
    }
}