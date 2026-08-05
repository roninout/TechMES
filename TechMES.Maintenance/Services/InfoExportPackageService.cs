using System.IO;
using System.Security.Cryptography;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Создаёт переносимый пакет:
///
/// Excel-файл
/// Supplier_logo
/// Instruction
/// Instruction\Images
/// Scheme
/// Scheme\Group
/// Scheme\Equipment
///
/// Перед заменой старого пакета новый полностью создаётся
/// во временном каталоге.
/// </summary>
public sealed class InfoExportPackageService
{
    public const string SupplierLogoFolderName = "Supplier_logo";
    public const string InstructionFolderName = "Instruction";
    public const string InstructionImageFolderName = "Images";
    public const string SchemeFolderName = "Scheme";
    public const string SchemeGroupFolderName = "Group";
    public const string SchemeEquipmentFolderName = "Equipment";

    private readonly InfoExportStore _store = new();
    private readonly ExcelInfoExportWriter _writer = new();

    /// <summary>
    /// Проверяет наличие предыдущего экспортного пакета.
    /// </summary>
    public bool PackageExists(string excelFilePath)
    {
        if (string.IsNullOrWhiteSpace(excelFilePath))
            return false;

        try
        {
            var fullExcelPath = NormalizeExcelPath(excelFilePath);
            var root = Path.GetDirectoryName(fullExcelPath)!;

            return
                File.Exists(fullExcelPath)
                || Directory.Exists(Path.Combine(root, SupplierLogoFolderName))
                || Directory.Exists(Path.Combine(root, InstructionFolderName))
                || Directory.Exists(Path.Combine(root, SchemeFolderName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Создаёт полный экспортный пакет и атомарно заменяет предыдущий.
    /// </summary>
    public async Task<InfoExportPackageResult> ExportAsync(string connectionString, string excelFilePath, RuntimeCatalogSnapshot? runtimeCatalog, CancellationToken cancellationToken = default)
    {
        var targetExcelPath = NormalizeExcelPath(excelFilePath);
        var targetRoot = Path.GetDirectoryName(targetExcelPath) ?? throw new InvalidOperationException("Excel export directory cannot be resolved.");
        Directory.CreateDirectory(targetRoot);

        var stageRoot = Path.Combine(targetRoot,$".techmes-info-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);

        try
        {
            var snapshot = await _store.LoadAsync(connectionString, cancellationToken);
            var warnings = new List<string>();
            var stageExcelPath = Path.Combine(stageRoot, Path.GetFileName(targetExcelPath));
            var supplierRoot = Path.Combine(stageRoot, SupplierLogoFolderName);
            var instructionRoot = Path.Combine(stageRoot, InstructionFolderName);
            var instructionImageRoot = Path.Combine(instructionRoot, InstructionImageFolderName);
            var schemeRoot = Path.Combine(stageRoot, SchemeFolderName);
            var schemeGroupRoot = Path.Combine(schemeRoot, SchemeGroupFolderName);
            var schemeEquipmentRoot = Path.Combine(schemeRoot, SchemeEquipmentFolderName);

            Directory.CreateDirectory(supplierRoot);
            Directory.CreateDirectory(instructionRoot);
            Directory.CreateDirectory(instructionImageRoot);
            Directory.CreateDirectory(schemeRoot);
            Directory.CreateDirectory(schemeGroupRoot);
            Directory.CreateDirectory(schemeEquipmentRoot);

            
            // Для каждой папки запоминаем уже занятые имена и их хеши. Это защищает от одинаковых имён файлов с разным содержимым.
            var usedNames = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var workbookSuppliers = await ExportSuppliersAsync(snapshot.Suppliers, supplierRoot, usedNames, warnings, cancellationToken);
            var instructionNameMap = await ExportNamedLibraryAsync(snapshot.InstructionFiles, instructionRoot, usedNames, "INSTRUCTION", cancellationToken);
            var photoNameMap = await ExportNamedLibraryAsync(snapshot.PhotoFiles, instructionImageRoot, usedNames, "PHOTO", cancellationToken);
            var workbookOrders = BuildWorkbookOrders(snapshot.Orders, instructionNameMap, photoNameMap, warnings);
            var workbookInstructions = BuildWorkbookInstructions(snapshot.EquipmentInfo, runtimeCatalog, warnings);
            var workbookSchemes = await ExportSchemesAsync(snapshot.SchemeFiles, schemeRoot, schemeGroupRoot, schemeEquipmentRoot, usedNames, warnings, cancellationToken);
            var workbookData = new InfoExportWorkbookData(InstructionFolderName, SchemeFolderName, 
                    CombineExcelPath(SchemeFolderName, SchemeGroupFolderName),
                    CombineExcelPath(SchemeFolderName,SchemeEquipmentFolderName),
                    workbookSuppliers, workbookOrders, workbookInstructions, workbookSchemes);

            await _writer.WriteAsync(stageExcelPath, workbookData, cancellationToken);

            ReplaceTargetPackage(stageRoot, targetExcelPath);

            var binaryFileCount = snapshot.Suppliers.Count(x =>
                    x.LogoData is { Length: > 0 })
                    + snapshot.InstructionFiles.Count
                    + snapshot.PhotoFiles.Count
                    + snapshot.SchemeFiles.Count;

            return new InfoExportPackageResult(targetExcelPath, snapshot.Suppliers.Count, snapshot.Orders.Count, workbookInstructions.Count, snapshot.SchemeFiles.Count, binaryFileCount, runtimeCatalog is not null, warnings);
        }
        finally
        {
            DeletePathIfExists(stageRoot);
        }
    }

    /// <summary>
    /// Экспортирует логотипы и формирует строки SUPPLIER.
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportWorkbookSupplierRow>>ExportSuppliersAsync(IReadOnlyList<InfoExportSupplierRow> suppliers, string targetDirectory, IDictionary<string, Dictionary<string, string>> usedNames, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        var result =
            new List<InfoExportWorkbookSupplierRow>();

        foreach (var supplier in suppliers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var logoReference = "";

            if (supplier.LogoData is { Length: > 0 })
            {
                var preferredName = string.IsNullOrWhiteSpace(supplier.LogoFileName) ? $"{supplier.Supplier}.bin" : supplier.LogoFileName;
                var exportedName = await WriteBinaryFileAsync(targetDirectory, preferredName, supplier.LogoFileHash, supplier.LogoData, usedNames, cancellationToken);
                logoReference = exportedName;
            }
            else if (!string.IsNullOrWhiteSpace(supplier.LogoFileName))
            {
                warnings.Add($"SUPPLIER '{supplier.Supplier}' contains " + $"logo file name '{supplier.LogoFileName}', " + "but logo_data is empty.");
            }

            result.Add(new InfoExportWorkbookSupplierRow(supplier.Supplier, logoReference));
        }

        return result;
    }

    /// <summary>
    /// Экспортирует библиотеку INSTRUCTION или PHOTO.
    ///
    /// Результат связывает исходное имя файла с именем,
    /// реально созданным в экспортном каталоге.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>>ExportNamedLibraryAsync(IReadOnlyList<InfoExportBinaryFile> files, string targetDirectory, IDictionary<string, Dictionary<string, string>> usedNames, string libraryName,CancellationToken cancellationToken)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalName =
                GetFileNameOnly(file.FileName);

            var exportedName =
                await WriteBinaryFileAsync(
                    targetDirectory,
                    originalName,
                    file.FileHash,
                    file.FileData,
                    usedNames,
                    cancellationToken);

            if (result.TryGetValue(
                    originalName,
                    out var existingName)
                && !string.Equals(
                    existingName,
                    exportedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{libraryName} contains several different files " +
                    $"with the same name '{originalName}'. " +
                    "ORDERS stores only file names and cannot distinguish them.");
            }

            result[originalName] =
                exportedName;
        }

        return result;
    }

    /// <summary>
    /// Формирует строки листа ORDERS.
    ///
    /// В Source и Image записываются только имена файлов.
    /// Физические файлы при этом остаются в папках Instruction
    /// и Instruction\Images экспортного пакета.
    /// </summary>
    private static IReadOnlyList<InfoExportWorkbookOrderRow> BuildWorkbookOrders(IReadOnlyList<InfoExportOrderRow> orders, IReadOnlyDictionary<string, string> instructionFiles, IReadOnlyDictionary<string, string> photoFiles, ICollection<string> warnings)
    {
        return orders
            .Select(order => new InfoExportWorkbookOrderRow(order.Type, order.ProductCode, order.Supplier,
                RewriteStoredFileList(order.Source, instructionFiles, $"ORDERS product '{order.ProductCode}' Source", warnings),
                order.Description,
                RewriteStoredFileList(order.Image, photoFiles, $"ORDERS product '{order.ProductCode}' Image", warnings)))
            .OrderBy(order => order.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(order => order.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Формирует INSTRUCTION.
    ///
    /// Station и Type не хранятся в PostgreSQL Info-модуля,
    /// поэтому при доступном Runtime они добавляются из его каталога.
    /// </summary>
    private static IReadOnlyList<InfoExportWorkbookInstructionRow>BuildWorkbookInstructions(IReadOnlyList<InfoExportEquipmentInfoRow> equipmentInfo, RuntimeCatalogSnapshot? runtimeCatalog, ICollection<string> warnings)
    {
        Dictionary<string, RuntimeCatalogEquipmentItem>?
            runtimeByEquipment = null;

        HashSet<string>? runtimeEquipmentNames = null;

        if (runtimeCatalog is not null)
        {
            runtimeByEquipment =
                runtimeCatalog.EquipmentItems
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(
                            x.Equipment))
                    .GroupBy(
                        x => x.Equipment.Trim(),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        x => x.Key,
                        x => x.First(),
                        StringComparer.OrdinalIgnoreCase);

            runtimeEquipmentNames =
                runtimeByEquipment.Keys
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            warnings.Add(
                "Runtime catalog was unavailable. " +
                "INSTRUCTION Station and Type were exported empty. " +
                "Only rows with product/document data were included.");
        }

        var selectedRows =
            runtimeEquipmentNames is not null
                ? equipmentInfo.Where(row =>
                    runtimeEquipmentNames.Contains(
                        row.Equipment))
                : equipmentInfo.Where(row =>
                    !string.IsNullOrWhiteSpace(
                        row.ProductCode)
                    || row.HasInstructionLinks
                    || row.HasPhotoLinks);

        var result =
            new List<InfoExportWorkbookInstructionRow>();

        foreach (var row in selectedRows)
        {
            var station = "";
            var type = "";

            if (runtimeByEquipment is not null
                && runtimeByEquipment.TryGetValue(
                    row.Equipment,
                    out var runtimeItem))
            {
                station =
                    runtimeItem.Station;

                type =
                    runtimeItem.Type;
            }

            result.Add(
                new InfoExportWorkbookInstructionRow(
                    station,
                    type,
                    row.Equipment,
                    row.ProductCode,
                    row.Supplier,
                    row.Description));
        }

        return result
            .OrderBy(
                row => row.Station,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                row => row.Type,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                row => row.Equipment,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Экспортирует SCHEME в соответствующие папки области назначения.
    ///
    /// Несколько Station / Group / Equipment, относящихся к одной схеме,
    /// сохраняются вместе в одной ячейке через "; ".
    ///
    /// Если несколько файлов имеют полностью одинаковый набор назначений,
    /// их имена объединяются в одной ячейке Source через "; ".
    /// </summary>
    private static async Task<IReadOnlyList<InfoExportWorkbookSchemeRow>> ExportSchemesAsync(IReadOnlyList<InfoExportSchemeFile> schemes, string stationDirectory, string groupDirectory, string equipmentDirectory, IDictionary<string, Dictionary<string, string>> usedNames, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        // key   = полный набор назначений одной схемы;
        // value = файлы, относящиеся к этому набору назначений.
        var stationSources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var groupSources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var equipmentSources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var scheme in schemes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stations = SplitStoredValues(scheme.Station);
            var groups = SplitStoredValues(scheme.GroupNames);
            var equipments = SplitStoredValues(scheme.Equipments);
            var hasScope = stations.Count > 0 || groups.Count > 0 || equipments.Count > 0;

            // Station scope.
            if (stations.Count > 0)
            {
                var exportedName = await WriteBinaryFileAsync(stationDirectory, scheme.FileName, scheme.FileHash, scheme.FileData, usedNames, cancellationToken);
                AddSource(stationSources, stations, exportedName);
            }

            // Group scope.
            if (groups.Count > 0)
            {
                var exportedName = await WriteBinaryFileAsync(groupDirectory, scheme.FileName, scheme.FileHash, scheme.FileData, usedNames, cancellationToken);
                AddSource(groupSources, groups, exportedName);
            }

            // Equipment scope.
            if (equipments.Count > 0)
            {
                var exportedName = await WriteBinaryFileAsync(equipmentDirectory, scheme.FileName, scheme.FileHash, scheme.FileData, usedNames, cancellationToken);
                AddSource(equipmentSources, equipments, exportedName);
            }

            // Файл без области назначения экспортируем физически,
            // но в лист SCHEME не включаем.
            if (!hasScope)
            {
                await WriteBinaryFileAsync(stationDirectory, scheme.FileName, scheme.FileHash, scheme.FileData, usedNames, cancellationToken);

                warnings.Add(
                    $"SCHEME '{scheme.FileName}' has no Station, Group or Equipment target. " +
                    "The file was exported, but it is not referenced by the SCHEME worksheet.");
            }
        }

        var result = new List<InfoExportWorkbookSchemeRow>();

        AddRows(result, InfoExportSchemeScope.Station, stationSources);
        AddRows(result, InfoExportSchemeScope.Group, groupSources);
        AddRows(result, InfoExportSchemeScope.Equipment, equipmentSources);

        return result;

        // Добавляет файл к полному набору назначений одной схемы.
        static void AddSource(IDictionary<string, List<string>> targetMap, IReadOnlyList<string> targets, string source)
        {
            if (targets.Count == 0 || string.IsNullOrWhiteSpace(source))
                return;

            // Не разбиваем список на отдельные строки.
            // Например: S02.R02; S02.R05 остаётся одной ячейкой.
            var targetKey = string.Join(
                "; ",
                targets
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(targetKey))
                return;

            if (!targetMap.TryGetValue(targetKey, out var sources))
            {
                sources = new List<string>();
                targetMap[targetKey] = sources;
            }

            if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase))
                sources.Add(source);
        }

        // Создаёт одну строку Excel на один полный набор назначений.
        static void AddRows(List<InfoExportWorkbookSchemeRow> result, InfoExportSchemeScope scope, IDictionary<string, List<string>> targetMap)
        {
            foreach (var item in targetMap.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var sources = string.Join(
                    "; ",
                    item.Value
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase));

                result.Add(new InfoExportWorkbookSchemeRow(scope, item.Key, sources));
            }
        }
    }

    /// <summary>
    /// Преобразует сохранённый список файлов в список имён,
    /// реально созданных в экспортном пакете.
    ///
    /// Пути к папкам в Excel не записываются.
    /// Несколько файлов разделяются через "; ".
    ///
    /// Если соответствующего бинарного файла нет в PostgreSQL,
    /// ссылка пропускается и добавляется предупреждение.
    /// </summary>
    private static string RewriteStoredFileList(string value, IReadOnlyDictionary<string, string> exportedFiles, string context, ICollection<string> warnings)
    {
        var sourceNames = SplitStoredValues(value);

        if (sourceNames.Count == 0)
            return "";

        var result = new List<string>();

        foreach (var storedValue in sourceNames)
        {
            // В БД может находиться как простое имя, так и старый полный путь.
            // Для поиска экспортированного файла используем только имя.
            var originalName = GetFileNameOnly(storedValue);

            if (string.IsNullOrWhiteSpace(originalName))
                continue;

            if (!exportedFiles.TryGetValue(originalName, out var exportedName))
            {
                var warning = $"{context} references '{originalName}', but this binary file is missing in PostgreSQL. The file reference was skipped.";

                if (!warnings.Contains(warning))
                    warnings.Add(warning);

                continue;
            }

            result.Add(exportedName);
        }

        return string.Join("; ", result.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Записывает бинарный файл с защитой от конфликтов имён.
    /// </summary>
    private static async Task<string> WriteBinaryFileAsync(string directory, string preferredFileName, string storedHash, byte[] data, IDictionary<string, Dictionary<string, string>> usedNames, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        var hash =
            string.IsNullOrWhiteSpace(storedHash)
                ? ComputeSha256(data)
                : storedHash.Trim().ToLowerInvariant();

        var safeName =
            SanitizeFileName(
                GetFileNameOnly(preferredFileName));

        if (string.IsNullOrWhiteSpace(safeName))
            safeName = $"{hash[..Math.Min(12, hash.Length)]}.bin";

        if (!usedNames.TryGetValue(
                directory,
                out var directoryNames))
        {
            directoryNames =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            usedNames[directory] =
                directoryNames;
        }

        var candidate =
            safeName;

        if (directoryNames.TryGetValue(
                candidate,
                out var existingHash))
        {
            if (string.Equals(
                    existingHash,
                    hash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            candidate =
                AddHashSuffix(
                    safeName,
                    hash);

            var counter = 2;

            while (directoryNames.TryGetValue(
                       candidate,
                       out existingHash)
                   && !string.Equals(
                       existingHash,
                       hash,
                       StringComparison.OrdinalIgnoreCase))
            {
                candidate =
                    AddHashSuffix(
                        safeName,
                        $"{hash}_{counter++}");
            }

            if (directoryNames.TryGetValue(
                    candidate,
                    out existingHash)
                && string.Equals(
                    existingHash,
                    hash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        var fullPath =
            Path.Combine(
                directory,
                candidate);

        await File.WriteAllBytesAsync(
            fullPath,
            data,
            cancellationToken);

        directoryNames[candidate] =
            hash;

        return candidate;
    }

    /// <summary>
    /// Атомарно заменяет предыдущий пакет через временный backup-каталог.
    ///
    /// При ошибке:
    /// 1) удаляются только новые элементы, которые действительно успели
    ///    установиться из staging;
    /// 2) старые элементы восстанавливаются из backup в обратном порядке;
    /// 3) если восстановление завершилось не полностью, backup сохраняется
    ///    для ручного восстановления.
    /// </summary>
    private static void ReplaceTargetPackage(string stageRoot, string targetExcelPath)
    {
        var targetRoot = Path.GetDirectoryName(targetExcelPath)!;
        var stageExcelPath = Path.Combine(stageRoot, Path.GetFileName(targetExcelPath));
        var replacements = new List<(string Source, string Target)>
            {
                (
                    stageExcelPath,
                    targetExcelPath
                ),
                (
                    Path.Combine(stageRoot, SupplierLogoFolderName),
                    Path.Combine(targetRoot, SupplierLogoFolderName)
                ),
                (
                    Path.Combine(stageRoot, InstructionFolderName),
                    Path.Combine(targetRoot, InstructionFolderName)
                ),
                (
                    Path.Combine(stageRoot, SchemeFolderName),
                    Path.Combine(targetRoot, SchemeFolderName)
                )
            };

        var backupRoot = Path.Combine(targetRoot, $".techmes-info-export-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);

        // Здесь хранятся только старые элементы, которые действительно были успешно перенесены в backup.
        var backups = new List<(string Backup, string Target)>();

        // Здесь хранятся только новые элементы, которые действительно были успешно перенесены из staging в целевую папку.
        var installedTargets = new List<string>();

        try
        {
            // Шаг 1. Переносим существующий экспортный пакет в backup.
            for (var index = 0; index < replacements.Count; index++)
            {
                var target = replacements[index].Target;

                if (!PathExists(target))
                    continue;

                var backup = Path.Combine(backupRoot, $"{index}_{Path.GetFileName(target)}");
                MovePath(target, backup);

                // Добавляем запись только после успешного MovePath.
                backups.Add((backup, target));
            }

            // Шаг 2. Устанавливаем новый пакет из staging.
            foreach (var replacement in replacements)
            {
                MovePath(replacement.Source, replacement.Target);

                // Добавляем target только после успешной установки.
                installedTargets.Add(replacement.Target);
            }
        }
        catch (Exception replacementException)
        {
            var rollbackErrors = new List<Exception>();

            // Сначала удаляем только те элементы нового пакета, которые действительно успели установиться.
            // Старые элементы, которые backup-цикл ещё не успел переместить, не затрагиваются.
            for (var index = installedTargets.Count - 1; index >= 0; index--)
            {
                var installedTarget = installedTargets[index];

                try
                {
                    DeletePathIfExists(installedTarget);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(new IOException("Failed to remove partially installed export path: " + installedTarget, ex));
                }
            }

            // Восстанавливаем старый пакет в обратном порядке.
            for (var index = backups.Count - 1; index >= 0; index--)
            {
                var backup = backups[index];

                if (!PathExists(backup.Backup))
                    continue;

                try
                {
                    // Не удаляем существующий target автоматически. Если он остался после неудачного удаления нового пакета, безопаснее сохранить backup и сообщить об ошибке.
                    if (PathExists(backup.Target))
                    {
                        throw new IOException("Cannot restore export backup because " + "the target still exists: " + backup.Target);
                    }

                    MovePath(backup.Backup, backup.Target);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(new IOException($"Failed to restore export backup " + $"'{backup.Backup}' to '{backup.Target}'.", ex));
                }
            }

            // Если хотя бы один элемент не удалось восстановить, backupRoot не удаляем.
            // В сообщении будет указан путь, откуда можно вручную восстановить старые файлы.
            if (rollbackErrors.Count > 0)
            {
                var allErrors = new List<Exception>{replacementException};
                allErrors.AddRange(rollbackErrors);
                throw new AggregateException("Export package replacement failed and automatic " + "rollback was incomplete. " + $"The remaining backup was preserved at: {backupRoot}", allErrors);
            }

            // Старый пакет полностью восстановлен. Удаляем оставшийся пустой backup-каталог.
            // Ошибка очистки пустой папки не должна скрывать первоначальную ошибку замены пакета.
            try
            {
                DeletePathIfExists(backupRoot);
            }
            catch
            {
                // Старый пакет уже восстановлен.
            }

            // Повторно выбрасываем исходную ошибку замены, сохраняя её первоначальный stack trace.
            throw;
        }

        // Новый пакет полностью установлен.
        // Очистку backup выполняем отдельно от блока замены: ошибка удаления backup не должна запускать rollback уже успешно установленного нового пакета.
        try
        {
            DeletePathIfExists(backupRoot);
        }
        catch (Exception cleanupException)
        {
            throw new IOException("The export package was replaced successfully, " + "but the previous package backup could not be removed. " + $"Backup path: {backupRoot}", cleanupException);
        }
    }

    private static string NormalizeExcelPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Excel export file path is empty.");
        }

        var fullPath =
            Path.GetFullPath(value.Trim().Trim('"'));

        if (!string.Equals(
                Path.GetExtension(fullPath),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Excel export file must have the .xlsx extension.");
        }

        return fullPath;
    }

    private static IReadOnlyList<string>SplitStoredValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(
                [';', ','],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(x =>
                !string.IsNullOrWhiteSpace(x))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetFileNameOnly(string value)
    {
        var cleanValue =
            value.Trim().Trim('"');

        try
        {
            var fileName =
                Path.GetFileName(cleanValue);

            return string.IsNullOrWhiteSpace(fileName)
                ? cleanValue
                : fileName;
        }
        catch
        {
            return cleanValue;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters =
            Path.GetInvalidFileNameChars()
                .ToHashSet();

        var result =
            new string(
                value
                    .Select(character =>
                        invalidCharacters.Contains(character)
                            ? '_'
                            : character)
                    .ToArray())
                .Trim()
                .TrimEnd('.', ' ');

        return result;
    }

    private static string AddHashSuffix(string fileName, string hash)
    {
        var extension =
            Path.GetExtension(fileName);

        var nameWithoutExtension =
            Path.GetFileNameWithoutExtension(fileName);

        var safeHash =
            SanitizeFileName(hash);

        var shortHash =
            safeHash[..Math.Min(
                12,
                safeHash.Length)];

        return
            $"{nameWithoutExtension}__{shortHash}{extension}";
    }

    private static string ComputeSha256(byte[] data)
    {
        return Convert
            .ToHexString(
                SHA256.HashData(data))
            .ToLowerInvariant();
    }

    private static string CombineExcelPath(params string[] parts)
    {
        return string.Join(
            "\\",
            parts
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x))
                .Select(x =>
                    x.Trim()
                        .Trim('\\', '/')));
    }

    private static bool PathExists(string path)
    {
        return
            File.Exists(path)
            || Directory.Exists(path);
    }

    private static void MovePath(string source, string target)
    {
        var targetDirectory =
            Path.GetDirectoryName(target);

        if (!string.IsNullOrWhiteSpace(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        if (Directory.Exists(source))
        {
            Directory.Move(
                source,
                target);

            return;
        }

        if (File.Exists(source))
        {
            File.Move(
                source,
                target);

            return;
        }

        throw new FileNotFoundException(
            $"Export package path was not found: {source}",
            source);
    }

    private static void DeletePathIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(
                path,
                recursive: true);
        }
    }
}