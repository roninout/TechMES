using System.Security.Cryptography;
using System.IO;
using Npgsql;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Read/write доступ Maintenance к справочникам Info-модуля в PostgreSQL.
/// Здесь намеренно повторяется логика старого WPF-импорта: supplier хранится отдельно, а order ссылается на supplier_id.
/// </summary>
public sealed class InfoImportEditStore
{
    /// <summary>
    /// Загружает вкладку SUPPLIER из public.equip_supplier.
    /// Бинарный logo_data не читается полностью, UI показывает только факт наличия логотипа и имя файла.
    /// </summary>
    public async Task<IReadOnlyList<ImportSupplierRowViewModel>> LoadSuppliersAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                name,
                COALESCE(logo_file_name, '') AS logo_file_name,
                logo_data,
                logo_data IS NOT NULL AND octet_length(logo_data) > 0 AS has_logo
            FROM public.equip_supplier
            ORDER BY name;
            """;

        var result = new List<ImportSupplierRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var hasLogo = reader.GetBoolean(3);
            var row = new ImportSupplierRowViewModel
            {
                Supplier = reader.GetString(0),
                LogoFileName = reader.GetString(1),
                LogoStatus = hasLogo ? "Stored" : "No logo"
            };

            if (!reader.IsDBNull(2))
                row.SetLogoPreview((byte[])reader.GetValue(2));

            result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// Сохраняет строки SUPPLIER.
    /// Если логотип не выбирался заново, существующие logo_data/logo_hash в БД не трогаются.
    /// </summary>
    public async Task<int> SaveSuppliersAsync(
        string connectionString,
        IEnumerable<ImportSupplierRowViewModel> suppliers,
        CancellationToken cancellationToken = default)
    {
        var clean = suppliers
            .Where(x => !string.IsNullOrWhiteSpace(x.Supplier))
            .GroupBy(x => x.Supplier.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        if (clean.Count == 0)
            return 0;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in clean)
            {
                if (item.LogoChanged)
                    await SaveSupplierWithLogoAsync(connection, transaction, item, cancellationToken);
                else
                    await SaveSupplierMetadataAsync(connection, transaction, item, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return clean.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Удаляет поставщиков, которые оператор убрал из SUPPLIER-таблицы и подтвердил кнопкой Save.
    /// Если поставщик связан с ORDERS или другими таблицами внешним ключом, PostgreSQL вернет ошибку, а Maintenance покажет ее оператору.
    /// </summary>
    public async Task<int> DeleteSuppliersAsync(
        string connectionString,
        IEnumerable<string> supplierNames,
        CancellationToken cancellationToken = default)
    {
        var clean = supplierNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (clean.Length == 0)
            return 0;

        const string sql = """
            DELETE FROM public.equip_supplier
            WHERE lower(name) = ANY(@names);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("names", clean);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Загружает вкладку ORDERS из public.equip_order и показывает supplier по имени, а не по supplier_id.
    /// </summary>
    public async Task<IReadOnlyList<ImportOrderRowViewModel>> LoadOrdersAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COALESCE(o.type, '') AS type,
                o.product_code,
                COALESCE(s.name, '') AS supplier,
                COALESCE(o.source, '') AS source,
                COALESCE(o.description, '') AS description,
                COALESCE(o.image, '') AS image
            FROM public.equip_order o
            LEFT JOIN public.equip_supplier s ON s.id = o.supplier_id
            ORDER BY o.type NULLS LAST, o.product_code;
            """;

        var result = new List<ImportOrderRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ImportOrderRowViewModel
            {
                Type = reader.GetString(0),
                ProductCode = reader.GetString(1),
                Supplier = reader.GetString(2),
                Source = reader.GetString(3),
                Description = reader.GetString(4),
                Image = reader.GetString(5)
            });
        }

        return result;
    }

    /// <summary>
    /// Сохраняет вкладку ORDERS.
    /// Для каждой строки с непустым Supplier сначала создаётся/находится поставщик, затем order обновляется по product_code.
    /// </summary>
    public async Task<int> SaveOrdersAsync(
        string connectionString,
        IEnumerable<ImportOrderRowViewModel> orders,
        CancellationToken cancellationToken = default)
    {
        var clean = orders
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
            .GroupBy(x => x.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        if (clean.Count == 0)
            return 0;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in clean)
            {
                var supplierId = await ResolveSupplierIdAsync(connection, transaction, item.Supplier, cancellationToken);
                await SaveOrderAsync(connection, transaction, item, supplierId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return clean.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Обновляет только имя поставщика и имя файла, не затрагивая существующие бинарные данные логотипа.
    /// </summary>
    /// <summary>
    /// Loads existing equipment-to-instruction links. Runtime data is merged in the UI layer.
    /// </summary>
    public async Task<IReadOnlyList<ImportInstructionRowViewModel>> LoadInstructionsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                i.equip_name,
                COALESCE(i.product_code, '') AS product_code,
                COALESCE(i.supplier, '') AS supplier,
                COALESCE(i.description, '') AS description,
                COALESCE(string_agg(ins.file_name, '; ' ORDER BY link.sort_order, ins.file_name), '') AS source
            FROM public.equip_info i
            LEFT JOIN public.equip_info_instruction link ON link.equip_name = i.equip_name
            LEFT JOIN public.equip_instruction ins ON ins.id = link.instruction_id
            GROUP BY i.equip_name, i.product_code, i.supplier, i.description
            ORDER BY i.equip_name;
            """;

        var result = new List<ImportInstructionRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ImportInstructionRowViewModel
            {
                Equipment = reader.GetString(0),
                ProductCode = reader.GetString(1),
                Supplier = reader.GetString(2),
                Description = reader.GetString(3),
                Source = reader.GetString(4)
            });
        }

        return result;
    }

    /// <summary>
    /// Saves instruction files and equipment links using the same library/link schema as the old WPF import.
    /// </summary>
    public async Task<int> SaveInstructionsAsync(
        string connectionString,
        string sourceRoot,
        IEnumerable<ImportInstructionRowViewModel> instructions,
        CancellationToken cancellationToken = default)
    {
        var clean = instructions
            .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
            .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        if (clean.Count == 0)
            return 0;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in clean)
            {
                await EnsureInfoRowAsync(
                    connection,
                    transaction,
                    item.Equipment,
                    item.ProductCode,
                    item.Supplier,
                    item.Description,
                    cancellationToken);

                /*
                 * Synchronize links, not just append new ones.
                 *
                 * If ORDERS.Source was changed from one PDF to another, the old
                 * instruction_id must be removed from equip_info_instruction before
                 * the current Source list is inserted.
                 */
                await DeleteInstructionLinksAsync(
                    connection,
                    transaction,
                    item.Equipment,
                    cancellationToken);

                await DeletePhotoLinksAsync(
                    connection,
                    transaction,
                    item.Equipment,
                    cancellationToken);

                var sortOrder = 0;
                foreach (var source in SplitSourceValues(item.Source))
                {
                    var filePath = ResolveSourceFilePath(sourceRoot, source);
                    var instructionId = await SaveLibraryFileAsync(
                        connection,
                        transaction,
                        "public.equip_instruction",
                        item.Type,
                        filePath,
                        cancellationToken);

                    await EnsureDocumentLinkAsync(
                        connection,
                        transaction,
                        "public.equip_info_instruction",
                        "instruction_id",
                        item.Equipment,
                        instructionId,
                        sortOrder++,
                        cancellationToken);
                }

                var photoSortOrder = 0;
                foreach (var image in SplitSourceValues(item.Image))
                {
                    var filePath = ResolveInstructionImageFilePath(sourceRoot, image);
                    var photoId = await SaveLibraryFileAsync(
                        connection,
                        transaction,
                        "public.equip_photo",
                        item.Type,
                        filePath,
                        cancellationToken);

                    await EnsureDocumentLinkAsync(
                        connection,
                        transaction,
                        "public.equip_info_photo",
                        "photo_id",
                        item.Equipment,
                        photoId,
                        photoSortOrder++,
                        cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return clean.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Loads scheme file library rows.
    /// </summary>
    public async Task<IReadOnlyList<ImportSchemeFileRowViewModel>> LoadSchemeFilesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COALESCE(equip_type_group, '') AS equip_type_group,
                COALESCE(file_name, '') AS file_name,
                COALESCE(display_name, '') AS display_name
            FROM public.equip_scheme
            ORDER BY equip_type_group, file_name;
            """;

        var result = new List<ImportSchemeFileRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ImportSchemeFileRowViewModel
            {
                Type = reader.GetString(0),
                Source = reader.GetString(1),
                Description = reader.GetString(2)
            });
        }

        return result;
    }

    /// <summary>
    /// Loads equipment-to-scheme links.
    /// </summary>
    public async Task<IReadOnlyList<ImportSchemeLinkRowViewModel>> LoadSchemeLinksAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                link.equip_name,
                COALESCE(scheme.equip_type_group, '') AS equip_type_group,
                COALESCE(scheme.file_name, '') AS file_name,
                COALESCE(scheme.display_name, '') AS display_name
            FROM public.equip_info_scheme link
            JOIN public.equip_scheme scheme ON scheme.id = link.scheme_id
            ORDER BY link.equip_name, link.sort_order, scheme.file_name;
            """;

        var result = new List<ImportSchemeLinkRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ImportSchemeLinkRowViewModel
            {
                Equipment = reader.GetString(0),
                Type = reader.GetString(1),
                Scheme = reader.GetString(2),
                Description = reader.GetString(3)
            });
        }

        return result;
    }

    /// <summary>
    /// Saves scheme library files and their equipment links.
    /// </summary>
    public async Task<int> SaveSchemesAsync(
        string connectionString,
        string sourceRoot,
        IEnumerable<ImportSchemeFileRowViewModel> files,
        IEnumerable<ImportSchemeLinkRowViewModel> links,
        CancellationToken cancellationToken = default)
    {
        var cleanFiles = files
            .Where(x => !string.IsNullOrWhiteSpace(x.Type))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source))
            .GroupBy(x => $"{x.Type.Trim()}\u001f{x.Source.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        var cleanLinks = links
            .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
            .Where(x => !string.IsNullOrWhiteSpace(x.Scheme))
            .ToList();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var file in cleanFiles)
            {
                var filePath = ResolveSourceFilePath(sourceRoot, file.Source);
                await SaveLibraryFileAsync(
                    connection,
                    transaction,
                    "public.equip_scheme",
                    file.Type,
                    filePath,
                    cancellationToken);
            }

            var sortOrderByEquipment = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in cleanLinks)
            {
                await EnsureInfoRowAsync(
                    connection,
                    transaction,
                    link.Equipment,
                    productCode: "",
                    supplier: "",
                    description: "",
                    cancellationToken);

                var schemeId = await ResolveLibraryFileIdAsync(
                    connection,
                    transaction,
                    "public.equip_scheme",
                    link.Type,
                    link.Scheme,
                    cancellationToken);

                var key = link.Equipment.Trim();
                sortOrderByEquipment.TryGetValue(key, out var sortOrder);
                sortOrderByEquipment[key] = sortOrder + 1;

                await EnsureDocumentLinkAsync(
                    connection,
                    transaction,
                    "public.equip_info_scheme",
                    "scheme_id",
                    link.Equipment,
                    schemeId,
                    sortOrder,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return cleanFiles.Count + cleanLinks.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task SaveSupplierMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportSupplierRowViewModel item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_supplier (name, logo_file_name, updated_at)
            VALUES (@name, NULLIF(@logo_file_name, ''), now())
            ON CONFLICT (name)
            DO UPDATE SET
                logo_file_name = COALESCE(NULLIF(EXCLUDED.logo_file_name, ''), public.equip_supplier.logo_file_name),
                updated_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", item.Supplier.Trim());
        command.Parameters.AddWithValue("logo_file_name", item.LogoFileName?.Trim() ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Сохраняет поставщика вместе с новым logo_data и SHA-256 hash, чтобы поведение совпадало со старым WPF-импортом.
    /// </summary>
    private static async Task SaveSupplierWithLogoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportSupplierRowViewModel item,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_supplier
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
                NULLIF(@logo_file_name, ''),
                @logo_file_hash,
                @logo_data,
                now()
            )
            ON CONFLICT (name)
            DO UPDATE SET
                logo_file_name = EXCLUDED.logo_file_name,
                logo_file_hash = EXCLUDED.logo_file_hash,
                logo_data = EXCLUDED.logo_data,
                updated_at = now();
            """;

        var logoData = item.PendingLogoData ?? [];
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", item.Supplier.Trim());
        command.Parameters.AddWithValue("logo_file_name", item.LogoFileName?.Trim() ?? "");
        command.Parameters.AddWithValue("logo_file_hash", logoData.Length == 0 ? DBNull.Value : ComputeSha256(logoData));
        command.Parameters.AddWithValue("logo_data", logoData.Length == 0 ? DBNull.Value : logoData);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Возвращает id поставщика для ORDERS.
    /// Если поставщик указан, но его ещё нет в public.equip_supplier, создаёт пустую строку поставщика.
    /// </summary>
    private static async Task<long?> ResolveSupplierIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string supplier,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplier))
            return null;

        const string sql = """
            INSERT INTO public.equip_supplier (name, updated_at)
            VALUES (@name, now())
            ON CONFLICT (name)
            DO UPDATE SET updated_at = public.equip_supplier.updated_at
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("name", supplier.Trim());
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
    }

    /// <summary>
    /// Обновляет заказ по product_code. Это тот же ключ, который использовался в старом Excel-импорте.
    /// </summary>
    private static async Task SaveOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportOrderRowViewModel item,
        long? supplierId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_order
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
                NULLIF(@type, ''),
                @product_code,
                @supplier_id,
                NULLIF(@description, ''),
                NULLIF(@source, ''),
                NULLIF(@image, ''),
                now()
            )
            ON CONFLICT (product_code)
            DO UPDATE SET
                type = EXCLUDED.type,
                supplier_id = EXCLUDED.supplier_id,
                description = EXCLUDED.description,
                source = EXCLUDED.source,
                image = EXCLUDED.image,
                updated_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("type", item.Type?.Trim() ?? "");
        command.Parameters.AddWithValue("product_code", item.ProductCode.Trim());
        command.Parameters.AddWithValue("supplier_id", supplierId is null ? DBNull.Value : supplierId.Value);
        command.Parameters.AddWithValue("description", item.Description?.Trim() ?? "");
        command.Parameters.AddWithValue("source", item.Source?.Trim() ?? "");
        command.Parameters.AddWithValue("image", item.Image?.Trim() ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Считает SHA-256 в hex-формате, как устойчивый признак выбранного файла логотипа.
    /// </summary>
    private static IEnumerable<string> SplitSourceValues(string? source)
    {
        return (source ?? "")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string ResolveSourceFilePath(string sourceRoot, string source)
    {
        var value = source.Trim();
        var filePath = Path.IsPathRooted(value)
            ? value
            : Path.Combine(sourceRoot ?? "", value);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Import source file not found: {filePath}", filePath);

        return filePath;
    }

    private static string ResolveInstructionImageFilePath(string sourceRoot, string image)
    {
        var value = image.Trim();
        if (Path.IsPathRooted(value))
            return ResolveSourceFilePath(sourceRoot ?? "", value);

        /*
         * The legacy WPF import keeps instruction images in an Images folder
         * next to instruction PDFs. The fallback supports manually selected
         * files that are placed directly in the configured source folder.
         */
        var imagesFolderPath = Path.Combine(sourceRoot ?? "", "Images", value);
        if (File.Exists(imagesFolderPath))
            return imagesFolderPath;

        return ResolveSourceFilePath(sourceRoot ?? "", value);
    }

    private static async Task EnsureInfoRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string equipmentName,
        string productCode,
        string supplier,
        string description,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.equip_info
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
                NULLIF(@product_code, ''),
                NULLIF(@supplier, ''),
                NULLIF(@description, ''),
                now()
            )
            ON CONFLICT (equip_name)
            DO UPDATE SET
                product_code = COALESCE(NULLIF(EXCLUDED.product_code, ''), public.equip_info.product_code),
                supplier = COALESCE(NULLIF(EXCLUDED.supplier, ''), public.equip_info.supplier),
                description = COALESCE(NULLIF(EXCLUDED.description, ''), public.equip_info.description),
                updated_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("equip_name", equipmentName.Trim());
        command.Parameters.AddWithValue("product_code", productCode?.Trim() ?? "");
        command.Parameters.AddWithValue("supplier", supplier?.Trim() ?? "");
        command.Parameters.AddWithValue("description", description?.Trim() ?? "");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> SaveLibraryFileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string type,
        string filePath,
        CancellationToken cancellationToken)
    {
        ValidateLibraryTableName(tableName);

        var fileName = Path.GetFileName(filePath);
        var fileData = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var fileHash = ComputeSha256(fileData);
        var updatedAt = File.GetLastWriteTime(filePath);

        var existingId = await FindLibraryFileIdAsync(
            connection,
            transaction,
            tableName,
            type,
            fileName,
            fileHash,
            cancellationToken);

        if (existingId is not null)
        {
            var updateSql = $"""
                UPDATE {tableName}
                SET
                    equip_type_group = NULLIF(@equip_type_group, ''),
                    file_name = @file_name,
                    display_name = @display_name,
                    file_hash = @file_hash,
                    file_data = @file_data,
                    updated_at = @updated_at
                WHERE id = @id;
                """;

            await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
            updateCommand.Parameters.AddWithValue("id", existingId.Value);
            AddLibraryFileParameters(updateCommand, type, fileName, fileHash, fileData, updatedAt);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return existingId.Value;
        }

        var insertSql = $"""
            INSERT INTO {tableName}
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
                NULLIF(@equip_type_group, ''),
                @file_name,
                @display_name,
                @file_hash,
                @file_data,
                @updated_at
            )
            RETURNING id;
            """;

        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
        AddLibraryFileParameters(insertCommand, type, fileName, fileHash, fileData, updatedAt);
        var scalar = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar);
    }

    private static async Task<long> ResolveLibraryFileIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string type,
        string fileName,
        CancellationToken cancellationToken)
    {
        ValidateLibraryTableName(tableName);

        var sql = $"""
            SELECT id
            FROM {tableName}
            WHERE lower(file_name) = lower(@file_name)
              AND (NULLIF(@equip_type_group, '') IS NULL OR lower(COALESCE(equip_type_group, '')) = lower(@equip_type_group))
            ORDER BY
                CASE WHEN lower(COALESCE(equip_type_group, '')) = lower(@equip_type_group) THEN 0 ELSE 1 END,
                id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("file_name", Path.GetFileName(fileName.Trim()));
        command.Parameters.AddWithValue("equip_type_group", type?.Trim() ?? "");
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null or DBNull)
            throw new InvalidOperationException($"Scheme file is not found in the library: {fileName}");

        return Convert.ToInt64(scalar);
    }

    private static async Task<long?> FindLibraryFileIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string type,
        string fileName,
        string fileHash,
        CancellationToken cancellationToken)
    {
        ValidateLibraryTableName(tableName);

        var sql = $"""
            SELECT id
            FROM {tableName}
            WHERE lower(COALESCE(equip_type_group, '')) = lower(@equip_type_group)
              AND (file_hash = @file_hash OR lower(file_name) = lower(@file_name))
            ORDER BY
                CASE WHEN file_hash = @file_hash THEN 0 ELSE 1 END,
                id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("equip_type_group", type?.Trim() ?? "");
        command.Parameters.AddWithValue("file_name", fileName);
        command.Parameters.AddWithValue("file_hash", fileHash);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
    }

    private static async Task DeleteInstructionLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string equipmentName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM public.equip_info_instruction
            WHERE equip_name = @equip_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("equip_name", equipmentName.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeletePhotoLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string equipmentName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM public.equip_info_photo
            WHERE equip_name = @equip_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("equip_name", equipmentName.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDocumentLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string linkTableName,
        string documentIdColumnName,
        string equipmentName,
        long documentId,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        ValidateLinkTableName(linkTableName, documentIdColumnName);

        var sql = $"""
            INSERT INTO {linkTableName}
            (
                equip_name,
                {documentIdColumnName},
                sort_order
            )
            VALUES
            (
                @equip_name,
                @document_id,
                @sort_order
            )
            ON CONFLICT (equip_name, {documentIdColumnName})
            DO UPDATE SET sort_order = EXCLUDED.sort_order;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("equip_name", equipmentName.Trim());
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("sort_order", sortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddLibraryFileParameters(
        NpgsqlCommand command,
        string type,
        string fileName,
        string fileHash,
        byte[] fileData,
        DateTime updatedAt)
    {
        command.Parameters.AddWithValue("equip_type_group", type?.Trim() ?? "");
        command.Parameters.AddWithValue("file_name", fileName);
        command.Parameters.AddWithValue("display_name", fileName);
        command.Parameters.AddWithValue("file_hash", fileHash);
        command.Parameters.AddWithValue("file_data", fileData);
        command.Parameters.AddWithValue("updated_at", updatedAt);
    }

    private static void ValidateLibraryTableName(string tableName)
    {
        if (tableName is not ("public.equip_photo" or "public.equip_instruction" or "public.equip_scheme"))
            throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported document library table.");
    }

    private static void ValidateLinkTableName(string tableName, string idColumnName)
    {
        if (tableName == "public.equip_info_instruction" && idColumnName == "instruction_id")
            return;

        if (tableName == "public.equip_info_photo" && idColumnName == "photo_id")
            return;

        if (tableName == "public.equip_info_scheme" && idColumnName == "scheme_id")
            return;

        throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported document link table.");
    }

    private static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
