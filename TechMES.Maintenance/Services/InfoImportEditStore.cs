using System.Security.Cryptography;
using System.IO;
using Npgsql;
using NpgsqlTypes;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Read/write доступ Maintenance к справочникам Info-модуля в PostgreSQL.
/// Здесь намеренно повторяется логика старого WPF-импорта: supplier хранится отдельно, а order ссылается на supplier_id.
/// </summary>
public sealed class InfoImportEditStore
{
    /// <summary>
    /// Нормализованная строка для пакетного сохранения общей информации об оборудовании.
    /// </summary>
    private sealed record InfoRow(
        string EquipmentName,
        string ProductCode,
        string Supplier,
        string Description);

    /// <summary>
    /// Связь оборудования с одним библиотечным файлом и ее порядок отображения.
    /// </summary>
    private sealed record DocumentLinkRow(
        string EquipmentName,
        long DocumentId,
        int SortOrder);

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
        string pdfSourceRoot,
        string imageSourceRoot,
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
            /*
             * Библиотечные файлы общие для всего оборудования. Кэш не дает
             * повторно читать и обновлять один PDF или рисунок для каждой
             * строки оборудования в рамках одной операции сохранения.
             */
            var instructionFileIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var photoFileIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var instructionLinks = new List<DocumentLinkRow>();
            var photoLinks = new List<DocumentLinkRow>();
            var equipmentNames = clean.Select(x => x.Equipment.Trim()).ToArray();

            await EnsureInfoRowsAsync(
                connection,
                transaction,
                clean.Select(item => new InfoRow(
                    item.Equipment,
                    item.ProductCode,
                    item.Supplier,
                    item.Description)),
                cancellationToken);

            /*
             * Сначала удаляем прежние связи сразу для всего набора оборудования.
             * После этого новые связи формируются в памяти и вставляются пакетно.
             */
            await DeleteDocumentLinksAsync(
                connection,
                transaction,
                "public.equip_info_instruction",
                "instruction_id",
                equipmentNames,
                cancellationToken);
            await DeleteDocumentLinksAsync(
                connection,
                transaction,
                "public.equip_info_photo",
                "photo_id",
                equipmentNames,
                cancellationToken);

            foreach (var item in clean)
            {
                var sortOrder = 0;
                foreach (var source in SplitSourceValues(item.Source))
                {
                    var filePath = ResolveSourceFilePath(pdfSourceRoot, source);
                    var instructionId = await SaveLibraryFileOnceAsync(
                        connection,
                        transaction,
                        "public.equip_instruction",
                        item.Type,
                        filePath,
                        instructionFileIds,
                        cancellationToken);

                    instructionLinks.Add(new DocumentLinkRow(
                        item.Equipment,
                        instructionId,
                        sortOrder++));
                }

                var photoSortOrder = 0;
                foreach (var image in SplitSourceValues(item.Image))
                {
                    var filePath = ResolveInstructionImageFilePath(
                        pdfSourceRoot,
                        imageSourceRoot,
                        image);
                    var photoId = await SaveLibraryFileOnceAsync(
                        connection,
                        transaction,
                        "public.equip_photo",
                        item.Type,
                        filePath,
                        photoFileIds,
                        cancellationToken);

                    photoLinks.Add(new DocumentLinkRow(
                        item.Equipment,
                        photoId,
                        photoSortOrder++));
                }
            }

            await EnsureDocumentLinksAsync(
                connection,
                transaction,
                "public.equip_info_instruction",
                "instruction_id",
                instructionLinks,
                cancellationToken);
            await EnsureDocumentLinksAsync(
                connection,
                transaction,
                "public.equip_info_photo",
                "photo_id",
                photoLinks,
                cancellationToken);

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
    /// Loads scheme file library rows from public.equip_scheme.
    /// </summary>
    public async Task<IReadOnlyList<ImportSchemeFileRowViewModel>> LoadSchemeFilesAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        const string sql = """
        SELECT
            COALESCE(equip_type_group, '') AS equip_type_group,
            COALESCE(file_name, '') AS file_name,
            COALESCE(display_name, '') AS display_name,
            COALESCE(station, '') AS station,
            COALESCE(group_names, '') AS group_names,
            COALESCE(equipments, '') AS equipments
        FROM public.equip_scheme
        ORDER BY
            lower(NULLIF(btrim(station), '')) NULLS LAST,
            lower(file_name);
        """;

        var result = new List<ImportSchemeFileRowViewModel>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureSchemeScopeColumnsAsync(connection, cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ImportSchemeFileRowViewModel
            {
                Type = reader.GetString(0),
                Source = reader.GetString(1),
                Description = reader.GetString(2),
                Station = reader.GetString(3),
                GroupNames = reader.GetString(4),
                Equipments = reader.GetString(5)
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
    /// Saves scheme library files, stores Station/Group/Equipment scope columns
    /// and rebuilds or extends public.equip_info_scheme links.
    ///
    /// preserveExistingTargetsAndLinks = false:
    ///     Manual SCHEME tab Save. UI table is the master source,
    ///     so scope columns and links are replaced.
    ///
    /// preserveExistingTargetsAndLinks = true:
    ///     Excel import. Existing DB scope columns and links are preserved,
    ///     Excel values are merged into station/group_names/equipments,
    ///     and only new links are added.
    /// </summary>
    public async Task<int> SaveSchemesAsync(string connectionString, string pdfSourceRoot, string imageSourceRoot, IEnumerable<ImportSchemeFileRowViewModel> files, IEnumerable<ImportSchemeLinkRowViewModel> links, bool preserveExistingTargetsAndLinks = false, CancellationToken cancellationToken = default)
    {
        var cleanFiles = MergeSchemeFileRows(files);

        var cleanLinks = links
            .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
            .Where(x => !string.IsNullOrWhiteSpace(x.Scheme))
            .GroupBy(
                x => $"{x.Equipment.Trim()}\u001f{Path.GetFileName(x.Scheme.Trim())}",
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToList();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemeScopeColumnsAsync(connection, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var schemeFileIdsByPathOrName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var schemeFileIdsByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var schemeIdsForCurrentRows = new HashSet<long>();

            foreach (var file in cleanFiles)
            {
                var schemeId = await ResolveOrSaveSchemeFileAsync(
                    connection,
                    transaction,
                    pdfSourceRoot,
                    imageSourceRoot,
                    file,
                    schemeFileIdsByPathOrName,
                    cancellationToken);

                schemeIdsForCurrentRows.Add(schemeId);

                var station = file.Station;
                var groupNames = file.GroupNames;
                var equipments = file.Equipments;

                /*
                 * Excel import must not wipe manually edited DB values.
                 * Therefore we merge DB scope columns with imported values.
                 */
                if (preserveExistingTargetsAndLinks)
                {
                    var existingScope = await LoadSchemeScopeAsync(
                        connection,
                        transaction,
                        schemeId,
                        cancellationToken);

                    station = MergeDelimitedText(new[] { existingScope.Station, file.Station });
                    groupNames = MergeDelimitedText(new[] { existingScope.GroupNames, file.GroupNames });
                    equipments = MergeDelimitedText(new[] { existingScope.Equipments, file.Equipments });
                }

                await UpdateSchemeScopeAsync(
                    connection,
                    transaction,
                    schemeId,
                    station,
                    groupNames,
                    equipments,
                    cancellationToken);

                var fileName = Path.GetFileName(file.Source.Trim());

                if (schemeFileIdsByName.TryGetValue(fileName, out var existingId)
                    && existingId != schemeId)
                {
                    throw new InvalidOperationException(
                        $"Several different scheme files have the same name in the import: {fileName}");
                }

                schemeFileIdsByName[fileName] = schemeId;
            }

            var sortOrderByEquipment = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var schemeLinks = new List<DocumentLinkRow>();

            await EnsureInfoRowsAsync(
                connection,
                transaction,
                cleanLinks.Select(link => new InfoRow(
                    link.Equipment,
                    ProductCode: "",
                    Supplier: "",
                    Description: "")),
                cancellationToken);

            foreach (var link in cleanLinks)
            {
                var schemeFileName = Path.GetFileName(link.Scheme.Trim());

                var schemeId = schemeFileIdsByName.TryGetValue(schemeFileName, out var importedSchemeId)
                    ? importedSchemeId
                    : await ResolveLibraryFileIdAsync(
                        connection,
                        transaction,
                        "public.equip_scheme",
                        link.Type,
                        schemeFileName,
                        cancellationToken);

                var key = link.Equipment.Trim();
                sortOrderByEquipment.TryGetValue(key, out var sortOrder);
                sortOrderByEquipment[key] = sortOrder + 1;

                schemeLinks.Add(new DocumentLinkRow(
                    link.Equipment,
                    schemeId,
                    sortOrder));
            }

            /*
             * Manual SCHEME tab Save is a full replacement.
             * Excel import is additive and must not remove existing DB links.
             */
            if (!preserveExistingTargetsAndLinks)
            {
                await DeleteSchemeLinksForSchemeIdsAsync(
                    connection,
                    transaction,
                    schemeIdsForCurrentRows,
                    cancellationToken);
            }

            await EnsureDocumentLinksAsync(
                connection,
                transaction,
                "public.equip_info_scheme",
                "scheme_id",
                schemeLinks,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return cleanFiles.Count + cleanLinks.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Current scope columns stored in public.equip_scheme.
    /// </summary>
    private sealed record SchemeScopeValues(string Station, string GroupNames, string Equipments);

    /// <summary>
    /// Reads existing Station/Group/Equipment scope columns for one scheme row.
    /// Used by Excel import merge mode so import does not wipe manual DB changes.
    /// </summary>
    private static async Task<SchemeScopeValues> LoadSchemeScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long schemeId, CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            COALESCE(station, '') AS station,
            COALESCE(group_names, '') AS group_names,
            COALESCE(equipments, '') AS equipments
        FROM public.equip_scheme
        WHERE id = @id
        LIMIT 1;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", schemeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SchemeScopeValues("", "", "");
        }

        return new SchemeScopeValues(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    /// <summary>
    /// Resolves one SCHEME row to public.equip_scheme.id.
    ///
    /// If the physical file exists, it is saved/updated in the library.
    /// If the physical file does not exist, an existing DB row with the same file_name is reused.
    /// This allows editing Station/Group/Equipment targets for already stored PDF files without
    /// requiring the original source folder to still contain the file.
    /// </summary>
    private static async Task<long> ResolveOrSaveSchemeFileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string pdfSourceRoot, string imageSourceRoot, ImportSchemeFileRowViewModel file, IDictionary<string, long> resolvedSchemeFileIds, CancellationToken cancellationToken)
    {
        var source = file.Source?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("SCHEME row has empty Source.");

        var physicalFilePath = TryResolveExistingSourceFilePath(
            [pdfSourceRoot, imageSourceRoot],
            source,
            out var checkedPaths);

        if (!string.IsNullOrWhiteSpace(physicalFilePath))
        {
            var cacheKey = Path.GetFullPath(physicalFilePath);

            if (resolvedSchemeFileIds.TryGetValue(cacheKey, out var cachedId))
                return cachedId;

            var savedId = await SaveLibraryFileAsync(
                connection,
                transaction,
                "public.equip_scheme",
                file.Type,
                physicalFilePath,
                cancellationToken);

            resolvedSchemeFileIds[cacheKey] = savedId;
            return savedId;
        }

        /*
         * Physical file was not found. This is normal for rows loaded from public.equip_scheme:
         * their Source is only file_name, while file_data is already stored in PostgreSQL.
         */
        var fileName = Path.GetFileName(source);
        var existingDbId = await FindSchemeFileIdByFileNameAsync(
            connection,
            transaction,
            fileName,
            cancellationToken);

        if (existingDbId is not null)
        {
            resolvedSchemeFileIds["db:" + fileName] = existingDbId.Value;
            return existingDbId.Value;
        }

        var checkedMessage = checkedPaths.Count == 0
            ? source
            : string.Join("; ", checkedPaths);

        throw new FileNotFoundException(
            $"Import source file not found and scheme library row does not exist in DB. Checked: {checkedMessage}",
            checkedPaths.FirstOrDefault() ?? source);
    }

    /// <summary>
    /// Tries to find a physical source file without throwing.
    /// Used by SCHEME Save because existing DB rows should not require the original PDF file.
    /// </summary>
    private static string? TryResolveExistingSourceFilePath(IEnumerable<string> sourceRoots, string source, out List<string> checkedPaths)
    {
        checkedPaths = [];

        var value = (source ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Path.IsPathRooted(value))
        {
            checkedPaths.Add(value);

            return File.Exists(value)
                ? Path.GetFullPath(value)
                : null;
        }

        foreach (var sourceRoot in sourceRoots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var candidate = Path.Combine(sourceRoot, value);
            checkedPaths.Add(candidate);

            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Finds an existing SCHEME library row by file_name.
    /// Used when the UI row was loaded from DB and the physical PDF source file is no longer available.
    /// </summary>
    private static async Task<long?> FindSchemeFileIdByFileNameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        const string sql = """
        SELECT id
        FROM public.equip_scheme
        WHERE lower(file_name) = lower(@file_name)
        ORDER BY id
        LIMIT 1;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("file_name", Path.GetFileName(fileName.Trim()));

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull
            ? null
            : Convert.ToInt64(scalar);
    }

    /// <summary>
    /// Adds new nullable scope columns to public.equip_scheme.
    /// This is intentionally safe to run on every Maintenance load/save.
    /// </summary>
    private static async Task EnsureSchemeScopeColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
        ALTER TABLE public.equip_scheme
        ADD COLUMN IF NOT EXISTS station text NULL;

        ALTER TABLE public.equip_scheme
        ADD COLUMN IF NOT EXISTS group_names text NULL;

        ALTER TABLE public.equip_scheme
        ADD COLUMN IF NOT EXISTS equipments text NULL;
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Merges duplicated scheme rows by Source.
    /// Excel import can produce many rows for the same physical scheme file,
    /// because one file can be linked to many stations/groups/equipment.
    /// </summary>
    private static List<ImportSchemeFileRowViewModel> MergeSchemeFileRows(IEnumerable<ImportSchemeFileRowViewModel> files)
    {
        return files
            .Where(x => !string.IsNullOrWhiteSpace(x.Source))
            .GroupBy(x => x.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var last = group.Last();

                return new ImportSchemeFileRowViewModel
                {
                    Type = MergeDelimitedText(group.Select(x => x.Type)),
                    Source = last.Source.Trim(),
                    Description = last.Description?.Trim() ?? "",
                    Station = MergeDelimitedText(group.Select(x => x.Station)),
                    GroupNames = MergeDelimitedText(group.Select(x => x.GroupNames)),
                    Equipments = MergeDelimitedText(group.Select(x => x.Equipments))
                };
            })
            .ToList();
    }

    /// <summary>
    /// Normalizes several comma/semicolon separated text cells into one semicolon separated text.
    /// </summary>
    private static string MergeDelimitedText(IEnumerable<string?> values)
    {
        return string.Join(
            "; ",
            values
                .SelectMany(SplitDelimitedValues)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitDelimitedValues(string? value)
    {
        return (value ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    /// <summary>
    /// Updates Station/Group/Equipment scope columns for one scheme library row.
    /// </summary>
    private static async Task UpdateSchemeScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long schemeId, string station, string groupNames, string equipments, CancellationToken cancellationToken)
    {
        const string sql = """
        UPDATE public.equip_scheme
        SET
            station = NULLIF(@station, ''),
            group_names = NULLIF(@group_names, ''),
            equipments = NULLIF(@equipments, ''),
            updated_at = now()
        WHERE id = @id;
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", schemeId);
        command.Parameters.AddWithValue("station", MergeDelimitedText([station]));
        command.Parameters.AddWithValue("group_names", MergeDelimitedText([groupNames]));
        command.Parameters.AddWithValue("equipments", MergeDelimitedText([equipments]));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Removes previous links for scheme files currently edited by the SCHEME tab.
    /// </summary>
    private static async Task DeleteSchemeLinksForSchemeIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<long> schemeIds,
        CancellationToken cancellationToken)
    {
        var clean = schemeIds
            .Distinct()
            .ToArray();

        if (clean.Length == 0)
            return;

        const string sql = """
        DELETE FROM public.equip_info_scheme
        WHERE scheme_id = ANY(@scheme_ids);
        """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(
            "scheme_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value = clean;

        await command.ExecuteNonQueryAsync(cancellationToken);
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
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Ищет файл в нескольких каталогах источников в заданном порядке.
    /// Абсолютный путь проверяется только один раз и не зависит от списка корней.
    /// </summary>
    private static string ResolveSourceFilePath(
        IEnumerable<string> sourceRoots,
        string source)
    {
        var value = source.Trim();
        if (Path.IsPathRooted(value))
            return ResolveSourceFilePath("", value);

        var attemptedPaths = new List<string>();
        foreach (var sourceRoot in sourceRoots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var candidate = Path.Combine(sourceRoot, value);
            attemptedPaths.Add(candidate);
            if (File.Exists(candidate))
                return candidate;
        }

        var attempted = attemptedPaths.Count == 0
            ? value
            : string.Join("; ", attemptedPaths);
        throw new FileNotFoundException(
            $"Import source file not found. Checked: {attempted}",
            attemptedPaths.FirstOrDefault() ?? value);
    }

    /// <summary>
    /// Ищет изображение инструкции в выделенном image-каталоге.
    /// Для совместимости со старой конфигурацией также проверяет Images рядом
    /// с PDF и сам PDF-каталог.
    /// </summary>
    private static string ResolveInstructionImageFilePath(
        string pdfSourceRoot,
        string imageSourceRoot,
        string image)
    {
        var value = image.Trim();
        if (Path.IsPathRooted(value))
            return ResolveSourceFilePath("", value);

        return ResolveSourceFilePath(
            [
                imageSourceRoot,
                Path.Combine(pdfSourceRoot ?? "", "Images"),
                pdfSourceRoot ?? ""
            ],
            value);
    }

    /// <summary>
    /// Одним запросом добавляет или обновляет equip_info для всего набора оборудования.
    /// Пустые значения не затирают уже заполненные поля в базе данных.
    /// </summary>
    private static async Task EnsureInfoRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<InfoRow> rows,
        CancellationToken cancellationToken)
    {
        var clean = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.EquipmentName))
            .GroupBy(x => x.EquipmentName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .ToArray();

        if (clean.Length == 0)
            return;

        const string sql = """
            INSERT INTO public.equip_info
            (
                equip_name,
                product_code,
                supplier,
                description,
                updated_at
            )
            SELECT
                source.equip_name,
                NULLIF(source.product_code, ''),
                NULLIF(source.supplier, ''),
                NULLIF(source.description, ''),
                now()
            FROM unnest
            (
                @equip_names::text[],
                @product_codes::text[],
                @suppliers::text[],
                @descriptions::text[]
            ) AS source(equip_name, product_code, supplier, description)
            ON CONFLICT (equip_name)
            DO UPDATE SET
                product_code = COALESCE(NULLIF(EXCLUDED.product_code, ''), public.equip_info.product_code),
                supplier = COALESCE(NULLIF(EXCLUDED.supplier, ''), public.equip_info.supplier),
                description = COALESCE(NULLIF(EXCLUDED.description, ''), public.equip_info.description),
                updated_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(
            "equip_names",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            clean.Select(x => x.EquipmentName.Trim()).ToArray();
        command.Parameters.Add(
            "product_codes",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            clean.Select(x => x.ProductCode?.Trim() ?? "").ToArray();
        command.Parameters.Add(
            "suppliers",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            clean.Select(x => x.Supplier?.Trim() ?? "").ToArray();
        command.Parameters.Add(
            "descriptions",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            clean.Select(x => x.Description?.Trim() ?? "").ToArray();
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

        /*
         * Совпадение хэша означает, что бинарный файл уже хранится в библиотеке.
         * Возвращаем его ID без UPDATE: это исключает повторную передачу bytea
         * в PostgreSQL для каждого оборудования и сохраняет исходную строку файла.
         */
        var existingHashId = await FindLibraryFileIdByHashAsync(
            connection,
            transaction,
            tableName,
            fileHash,
            cancellationToken);

        if (existingHashId is not null)
            return existingHashId.Value;

        /*
         * Если имя и тип прежние, но содержимое изменилось, обновляем существующую
         * логическую запись. В этом случае запись file_data действительно нужна.
         */
        var existingLogicalId = await FindLibraryFileIdByLogicalKeyAsync(
            connection,
            transaction,
            tableName,
            type,
            fileName,
            cancellationToken);

        if (existingLogicalId is not null)
        {
            var updateSql = $"""
                UPDATE {tableName}
                SET
                    equip_type_group = COALESCE(equip_type_group, NULLIF(@equip_type_group, '')),
                    file_name = @file_name,
                    display_name = @display_name,
                    file_hash = @file_hash,
                    file_data = @file_data,
                    updated_at = @updated_at
                WHERE id = @id;
                """;

            await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
            updateCommand.Parameters.AddWithValue("id", existingLogicalId.Value);
            AddLibraryFileParameters(updateCommand, type, fileName, fileHash, fileData, updatedAt);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return existingLogicalId.Value;
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

    /// <summary>
    /// Сохраняет библиотечный файл не более одного раза за текущую операцию.
    /// Ключ строится по полному пути без учета регистра; оборудование затем
    /// получает отдельную связь с уже сохраненной строкой библиотеки.
    /// </summary>
    private static async Task<long> SaveLibraryFileOnceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string type,
        string filePath,
        IDictionary<string, long> savedFileIds,
        CancellationToken cancellationToken)
    {
        var cacheKey = Path.GetFullPath(filePath);
        if (savedFileIds.TryGetValue(cacheKey, out var savedId))
            return savedId;

        savedId = await SaveLibraryFileAsync(
            connection,
            transaction,
            tableName,
            type,
            filePath,
            cancellationToken);
        savedFileIds[cacheKey] = savedId;
        return savedId;
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

    /// <summary>
    /// Ищет уже сохраненный бинарный файл по его SHA-256.
    /// </summary>
    private static async Task<long?> FindLibraryFileIdByHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string fileHash,
        CancellationToken cancellationToken)
    {
        ValidateLibraryTableName(tableName);

        var sql = $"""
            SELECT id
            FROM {tableName}
            WHERE file_hash = @file_hash
            ORDER BY id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("file_hash", fileHash);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
    }

    /// <summary>
    /// Ищет строку библиотеки по ее логическому ключу, когда файл с тем же именем
    /// был изменен и должен заменить прежнее содержимое.
    /// </summary>
    private static async Task<long?> FindLibraryFileIdByLogicalKeyAsync(
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
            WHERE lower(COALESCE(equip_type_group, '')) = lower(@equip_type_group)
              AND lower(file_name) = lower(@file_name)
            ORDER BY id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("equip_type_group", type?.Trim() ?? "");
        command.Parameters.AddWithValue("file_name", fileName);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? null : Convert.ToInt64(scalar);
    }

    /// <summary>
    /// Одним запросом удаляет старые связи для всего изменяемого оборудования.
    /// </summary>
    private static async Task DeleteDocumentLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string linkTableName,
        string documentIdColumnName,
        IEnumerable<string> equipmentNames,
        CancellationToken cancellationToken)
    {
        ValidateLinkTableName(linkTableName, documentIdColumnName);

        var clean = equipmentNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (clean.Length == 0)
            return;

        var sql = $"""
            DELETE FROM {linkTableName}
            WHERE equip_name = ANY(@equip_names);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(
            "equip_names",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value = clean;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Одним INSERT ... SELECT FROM unnest добавляет или обновляет набор связей.
    /// </summary>
    private static async Task EnsureDocumentLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string linkTableName,
        string documentIdColumnName,
        IEnumerable<DocumentLinkRow> links,
        CancellationToken cancellationToken)
    {
        ValidateLinkTableName(linkTableName, documentIdColumnName);

        var clean = links
            .Where(x => !string.IsNullOrWhiteSpace(x.EquipmentName))
            .GroupBy(
                x => (Equipment: x.EquipmentName.Trim().ToUpperInvariant(), x.DocumentId))
            .Select(x => x.Last())
            .ToArray();

        if (clean.Length == 0)
            return;

        var sql = $"""
            INSERT INTO {linkTableName}
            (
                equip_name,
                {documentIdColumnName},
                sort_order
            )
            SELECT
                source.equip_name,
                source.document_id,
                source.sort_order
            FROM unnest
            (
                @equip_names::text[],
                @document_ids::bigint[],
                @sort_orders::integer[]
            ) AS source(equip_name, document_id, sort_order)
            ON CONFLICT (equip_name, {documentIdColumnName})
            DO UPDATE SET sort_order = EXCLUDED.sort_order;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(
            "equip_names",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            clean.Select(x => x.EquipmentName.Trim()).ToArray();
        command.Parameters.Add(
            "document_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
            clean.Select(x => x.DocumentId).ToArray();
        command.Parameters.Add(
            "sort_orders",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            clean.Select(x => x.SortOrder).ToArray();
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
