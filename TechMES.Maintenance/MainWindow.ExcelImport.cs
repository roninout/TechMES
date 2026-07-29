using System.IO;
using System.Windows;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    /// <summary>
    /// Выполняет полный Excel-импорт информационного модуля.
    ///
    /// До первой записи в PostgreSQL метод читает весь файл, загружает каталог
    /// Runtime, раскрывает связи SCHEME и проверяет наличие каждого PDF,
    /// изображения и логотипа. Это защищает БД от частичного импорта из-за
    /// опечатки в Excel или отсутствующего файла.
    /// </summary>
    private async void OnImportExcelClick(object sender, RoutedEventArgs e)
    {
        if (IsExcelImportRunning)
            return;

        if (string.IsNullOrWhiteSpace(ExcelImportFilePath)
            || !File.Exists(ExcelImportFilePath))
        {
            ImportExcelStatusText = "Select an existing Excel import file first.";
            MessageBox.Show(
                this,
                ImportExcelStatusText,
                "IMPORT",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        IsExcelImportRunning = true;
        ImportExcelStatusText = "Reading and validating Excel import...";
        AppendDiagnostics(ImportExcelStatusText);

        try
        {
            PersistImportEditOptions();

            var excelFilePath = Path.GetFullPath(ExcelImportFilePath);
            var document = await Task.Run(() => _excelInfoImportReader.Read(excelFilePath));

            if (!await EnsureRuntimeCatalogForImportAsync())
                return;

            var prepared = await PrepareExcelImportAsync(
                excelFilePath,
                document,
                _importRuntimeCatalog!);

            ImportExcelStatusText = "Validation completed. Saving SUPPLIER...";
            var connectionString = GetRuntimeDatabaseConnectionString();

            var suppliersSaved = await _infoImportEditStore.SaveSuppliersAsync(
                connectionString,
                prepared.Suppliers);

            ImportExcelStatusText = "SUPPLIER saved. Saving ORDERS...";
            var ordersSaved = await _infoImportEditStore.SaveOrdersAsync(
                connectionString,
                prepared.Orders);

            ImportExcelStatusText = "ORDERS saved. Saving INSTRUCTION...";
            var instructionsSaved = await _infoImportEditStore.SaveInstructionsAsync(
                connectionString,
                "",
                "",
                prepared.Instructions);

            ImportExcelStatusText = "INSTRUCTION saved. Saving SCHEME...";
            var schemesSaved = await _infoImportEditStore.SaveSchemesAsync(
                connectionString,
                "",
                "",
                prepared.SchemeFiles,
                prepared.SchemeLinks);

            await RefreshImportSuppliersAsync();
            await RefreshImportOrdersAsync();
            await RefreshImportInstructionsAsync();
            await RefreshImportSchemesAsync();

            ImportExcelStatusText =
                $"Import completed: SUPPLIER {suppliersSaved}, ORDERS {ordersSaved}, "
                + $"INSTRUCTION {instructionsSaved}, SCHEME {schemesSaved}.";

            AppendDiagnostics(ImportExcelStatusText);
            MessageBox.Show(
                this,
                ImportExcelStatusText,
                "IMPORT",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ImportExcelStatusText = $"Excel import failed: {ex.Message}";
            AppendDiagnostics(ImportExcelStatusText);
            MessageBox.Show(
                this,
                ImportExcelStatusText,
                "IMPORT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsExcelImportRunning = false;
        }
    }

    /// <summary>
    /// Преобразует строки Excel в модели существующих вкладок Import/Edit.
    /// Все пути заменяются на абсолютные до вызова методов сохранения, поэтому
    /// SQL-слой не зависит от текущей рабочей папки Maintenance.
    /// </summary>
    private async Task<PreparedExcelImport> PrepareExcelImportAsync(
        string excelFilePath,
        ExcelInfoImportDocument document,
        RuntimeCatalogSnapshot runtime)
    {
        var errors = new List<string>();
        var workbookDirectory = Path.GetDirectoryName(excelFilePath) ?? "";

        /*
         * Runtime возвращает уже сгруппированные WEB-типы (DI, AI, VGA и т.д.),
         * а Excel хранит исходные SCADA-типы (DigitalIn, AnalogIn, ValveA).
         * Оба представления заранее приводятся к одному каноническому значению,
         * иначе корректная пара DigitalIn -> DI ошибочно блокирует импорт.
         */
        var runtimeTypes = runtime.Types
            .Select(NormalizeEquipmentTypeAlias)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeStations = runtime.Stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var equipmentByName = runtime.EquipmentItems
            .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var groupsByName = runtime.GroupItems
            .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var supplierRows = new List<ImportSupplierRowViewModel>();
        foreach (var source in document.Suppliers)
        {
            var supplierName = source.Supplier.Trim();
            if (string.IsNullOrWhiteSpace(supplierName))
                continue;

            var row = new ImportSupplierRowViewModel
            {
                Supplier = supplierName,
                LogoFileName = source.LogoFileName.Trim(),
                LogoStatus = string.IsNullOrWhiteSpace(source.LogoFileName)
                    ? "No logo"
                    : "Selected"
            };

            if (!string.IsNullOrWhiteSpace(source.LogoFileName))
            {
                var logoPath = ResolveRequiredImportFile(
                    source.LogoFileName,
                    "SUPPLIER logo",
                    errors,
                    SupplierLogoSourceRoot,
                    workbookDirectory);

                if (logoPath is not null)
                {
                    var logoData = await File.ReadAllBytesAsync(logoPath);
                    row.LogoFileName = Path.GetFileName(logoPath);
                    row.PendingLogoData = logoData;
                    row.LogoChanged = true;
                    row.SetLogoPreview(logoData);
                }
            }

            supplierRows.Add(row);
        }

        var supplierNames = supplierRows
            .Select(x => x.Supplier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var instructionFallbackRoot = ResolveWorkbookRoot(
            workbookDirectory,
            document.InstructionRoot);

        var orderRows = new List<ImportOrderRowViewModel>();
        foreach (var source in document.Orders)
        {
            ValidateRuntimeType(source.Type, "ORDERS", runtimeTypes, errors);

            if (!string.IsNullOrWhiteSpace(source.Supplier)
                && !supplierNames.Contains(source.Supplier.Trim()))
            {
                errors.Add(
                    $"ORDERS product '{source.ProductCode}': supplier "
                    + $"'{source.Supplier}' is missing on SUPPLIER sheet.");
            }

            var pdfSources = ResolveRequiredImportFiles(
                source.Source,
                $"ORDERS product '{source.ProductCode}' PDF",
                errors,
                OrdersPdfSourceRoot,
                instructionFallbackRoot,
                workbookDirectory);

            var imageSources = ResolveRequiredImportFiles(
                source.Image,
                $"ORDERS product '{source.ProductCode}' image",
                errors,
                InstructionImageSourceRoot,
                CombineIfPresent(instructionFallbackRoot, "Images"),
                instructionFallbackRoot,
                workbookDirectory);

            orderRows.Add(new ImportOrderRowViewModel
            {
                Type = source.Type.Trim(),
                ProductCode = source.ProductCode.Trim(),
                Supplier = source.Supplier.Trim(),
                Source = string.Join(";", pdfSources),
                Description = source.Description.Trim(),
                Image = string.Join(";", imageSources)
            });
        }

        var orderByProductCode = orderRows
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
            .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var instructionRows = new List<ImportInstructionRowViewModel>();
        foreach (var source in document.Instructions)
        {
            if (!equipmentByName.TryGetValue(source.Equipment.Trim(), out var equipment))
            {
                errors.Add(
                    $"INSTRUCTION: equipment '{source.Equipment}' was not found in Runtime.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(source.Station)
                && !runtimeStations.Contains(source.Station.Trim()))
            {
                errors.Add(
                    $"INSTRUCTION equipment '{source.Equipment}': station "
                    + $"'{source.Station}' was not found in Runtime.");
            }

            if (!string.IsNullOrWhiteSpace(source.Station)
                && !string.Equals(
                    source.Station.Trim(),
                    equipment.Station,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"INSTRUCTION equipment '{source.Equipment}': station "
                    + $"'{source.Station}' does not match Runtime station '{equipment.Station}'.");
            }

            if (!string.IsNullOrWhiteSpace(source.Type)
                && !string.Equals(
                    NormalizeEquipmentTypeAlias(source.Type),
                    NormalizeEquipmentTypeAlias(equipment.Type),
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"INSTRUCTION equipment '{source.Equipment}': type "
                    + $"'{source.Type}' does not match Runtime type '{equipment.Type}'.");
            }

            var row = new ImportInstructionRowViewModel
            {
                Station = equipment.Station,
                Type = equipment.Type,
                Equipment = equipment.Equipment,
                ProductCode = source.ProductCode.Trim(),
                Supplier = source.Supplier.Trim(),
                Description = source.Description.Trim()
            };

            /*
             * Лист INSTRUCTION одновременно является полным перечнем оборудования,
             * поэтому пустой Product code допустим. Связь с ORDERS и связанные
             * списки PDF/изображений добавляются только для непустого кода продукта.
             */
            if (!string.IsNullOrWhiteSpace(source.ProductCode))
            {
                if (!orderByProductCode.TryGetValue(source.ProductCode.Trim(), out var order))
                {
                    errors.Add(
                        $"INSTRUCTION equipment '{source.Equipment}': product code "
                        + $"'{source.ProductCode}' was not found on ORDERS sheet.");
                    continue;
                }

                row.ApplyOrder(order, source.ProductCode);
            }

            instructionRows.Add(row);
        }

        var schemeFiles = new List<ImportSchemeFileRowViewModel>();
        var schemeLinks = new List<ImportSchemeLinkRowViewModel>();
        foreach (var source in document.Schemes)
        {
            var sourceRoot = ResolveWorkbookRoot(workbookDirectory, source.SourceRoot);
            var configuredScopeRoot = ResolveConfiguredSchemeScopeRoot(
                SchemePdfSourceRoot,
                source.Scope);
            var files = ResolveRequiredImportFiles(
                source.Source,
                $"SCHEME target '{source.Target}'",
                errors,
                configuredScopeRoot,
                SchemePdfSourceRoot,
                SchemeImageSourceRoot,
                sourceRoot,
                workbookDirectory);

            var targets = ExpandSchemeTargets(
                source,
                runtime,
                equipmentByName,
                groupsByName,
                errors);

            foreach (var target in targets)
            {
                foreach (var file in files)
                {
                    var schemeName = Path.GetFileName(file);

                    schemeFiles.Add(new ImportSchemeFileRowViewModel
                    {
                        Type = target.Type,
                        Source = file,
                        Description = schemeName
                    });

                    schemeLinks.Add(new ImportSchemeLinkRowViewModel
                    {
                        Station = target.Station,
                        Type = target.Type,
                        Equipment = target.Equipment,
                        Scheme = schemeName,
                        Description = schemeName
                    });
                }
            }
        }

        if (supplierRows.Count == 0)
            errors.Add("SUPPLIER sheet does not contain import rows.");
        if (orderRows.Count == 0)
            errors.Add("ORDERS sheet does not contain import rows.");
        if (instructionRows.Count == 0)
            errors.Add("INSTRUCTION sheet does not contain valid Runtime links.");

        ThrowImportValidationErrors(errors);

        return new PreparedExcelImport(
            supplierRows,
            orderRows,
            instructionRows,
            schemeFiles
                .GroupBy(
                    x => $"{x.Type}\u001f{x.Source}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList(),
            schemeLinks
                .GroupBy(
                    x => $"{x.Equipment}\u001f{x.Type}\u001f{x.Scheme}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList());
    }

    /// <summary>
    /// Раскрывает назначение строки SCHEME до конкретных узлов Runtime.
    /// Station связывает схему со всем оборудованием станции, Group — с
    /// групповым узлом, Equipment — с точным именем оборудования.
    /// В одной Excel-ячейке разрешено перечислять несколько назначений через
    /// запятую или точку с запятой; каждое имя проверяется независимо.
    /// </summary>
    private static IReadOnlyList<RuntimeCatalogEquipmentItem> ExpandSchemeTargets(
        ExcelSchemeImportRow source,
        RuntimeCatalogSnapshot runtime,
        IReadOnlyDictionary<string, RuntimeCatalogEquipmentItem> equipmentByName,
        IReadOnlyDictionary<string, RuntimeCatalogEquipmentItem> groupsByName,
        ICollection<string> errors)
    {
        var targets = new List<RuntimeCatalogEquipmentItem>();

        foreach (var targetName in SplitSchemeTargetNames(source.Target))
        {
            IReadOnlyList<RuntimeCatalogEquipmentItem> resolvedTargets = source.Scope switch
            {
                ExcelSchemeScope.Station => runtime.EquipmentItems
                    .Where(x => string.Equals(
                        x.Station,
                        targetName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList(),

                ExcelSchemeScope.Group when groupsByName.TryGetValue(targetName, out var group) =>
                    [group],

                ExcelSchemeScope.Equipment when equipmentByName.TryGetValue(
                    targetName,
                    out var equipment) =>
                    [equipment],

                _ => []
            };

            if (resolvedTargets.Count == 0)
            {
                errors.Add(
                    $"SCHEME {source.Scope} target '{targetName}' was not found in Runtime.");
                continue;
            }

            targets.AddRange(resolvedTargets);
        }

        /*
         * Одна станция или несколько перечисленных назначений могут привести
         * к одному узлу. Перед сохранением связи оставляем только один экземпляр.
         */
        return targets
            .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    /// <summary>
    /// Разбивает содержимое ячейки назначения SCHEME на отдельные имена.
    /// Поддерживаются оба разделителя, используемые в Excel и ручном вводе:
    /// запятая и точка с запятой.
    /// </summary>
    private static IReadOnlyList<string> SplitSchemeTargetNames(string? value)
    {
        return (value ?? "")
            .Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Проверяет тип Excel по каноническому списку типов Runtime.
    /// </summary>
    private static void ValidateRuntimeType(
        string type,
        string sheet,
        IReadOnlySet<string> runtimeTypes,
        ICollection<string> errors)
    {
        var normalizedType = NormalizeEquipmentTypeAlias(type);

        if (string.IsNullOrWhiteSpace(normalizedType)
            || !runtimeTypes.Contains(normalizedType))
        {
            errors.Add($"{sheet}: type '{type}' was not found in Runtime.");
        }
    }

    /// <summary>
    /// Приводит исходный SCADA-тип и короткий WEB-тип к одному значению.
    /// Набор алиасов повторяет резервное сопоставление Runtime-каталога:
    /// например, DigitalIn/DigitalInSiemens/DI становятся DI.
    /// </summary>
    private static string NormalizeEquipmentTypeAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        /*
         * Удаление пробелов, подчёркиваний и других разделителей позволяет
         * одинаково обработать ValveA_EL, ValveA EL и VGA_EL.
         */
        var normalized = new string(
            value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        return normalized switch
        {
            "EQUIPMENT" or "EQ" => "EQUIPMENT",

            "DIGITALIN" or "DIGITALINSIEMENS" or "DI" => "DI",
            "DIGITALOUT" or "DIGITALOUTSIEMENS" or "DO" => "DO",

            "ANALOGIN"
                or "ANALOGINSIEMENS"
                or "ANALOGINCALC"
                or "ANALOGINCALCSIEMENS"
                or "AI" => "AI",

            "MOTOR" or "MOTORSIEMENS" or "M" => "MOTOR",

            "VALVEA" or "VALVEASIEMENS" or "VGA" => "VGA",
            "VALVEAEL" or "VGAEL" or "EL" => "VGA_EL",

            "VALVED" or "VALVEDSIEMENS" or "VGD" => "VGD",
            "ATV" or "ATVSIEMENS" => "ATV",

            _ => normalized
        };
    }

    /// <summary>
    /// Разрешает список имён, разделённых запятой или точкой с запятой,
    /// в абсолютные существующие пути.
    /// </summary>
    private static IReadOnlyList<string> ResolveRequiredImportFiles(
        string value,
        string context,
        ICollection<string> errors,
        params string?[] roots)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var result = new List<string>();
        foreach (var part in value.Split(
                     [';', ','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = ResolveRequiredImportFile(part, context, errors, roots);
            if (path is not null)
                result.Add(path);
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Ищет один файл сначала как абсолютный путь, затем во всех заданных
    /// каталогах. Ошибка добавляется в общий отчёт preflight.
    /// </summary>
    private static string? ResolveRequiredImportFile(
        string value,
        string context,
        ICollection<string> errors,
        params string?[] roots)
    {
        var fileName = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            return Path.GetFullPath(fileName);

        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var candidate = Path.Combine(root!, fileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        errors.Add($"{context}: file '{value}' was not found.");
        return null;
    }

    /// <summary>
    /// Делает путь из Excel абсолютным относительно папки книги.
    /// </summary>
    private static string ResolveWorkbookRoot(
        string workbookDirectory,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var root = value.Trim().Trim('"');
        return Path.IsPathRooted(root)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(workbookDirectory, root));
    }

    /// <summary>
    /// Безопасно объединяет каталог и относительную подпапку.
    /// </summary>
    private static string CombineIfPresent(string root, string child)
    {
        return string.IsNullOrWhiteSpace(root)
            ? ""
            : Path.Combine(root, child);
    }

    /// <summary>
    /// Возвращает настроенный каталог PDF для конкретного блока листа SCHEME.
    /// Станционные схемы находятся в корне, групповые — в Group, а схемы
    /// отдельного оборудования — в Equipment.
    /// </summary>
    private static string ResolveConfiguredSchemeScopeRoot(
        string schemeRoot,
        ExcelSchemeScope scope)
    {
        return scope switch
        {
            ExcelSchemeScope.Group => CombineIfPresent(schemeRoot, "Group"),
            ExcelSchemeScope.Equipment => CombineIfPresent(schemeRoot, "Equipment"),
            _ => schemeRoot
        };
    }

    /// <summary>
    /// Формирует компактное сообщение обо всех найденных проблемах.
    /// </summary>
    private static void ThrowImportValidationErrors(IReadOnlyCollection<string> errors)
    {
        if (errors.Count == 0)
            return;

        const int visibleErrorLimit = 15;
        var visibleErrors = errors.Take(visibleErrorLimit);
        var message = "Import validation failed:\n- " + string.Join("\n- ", visibleErrors);

        if (errors.Count > visibleErrorLimit)
            message += $"\n... and {errors.Count - visibleErrorLimit} more error(s).";

        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Полностью подготовленный пакет импорта. После его создания все
    /// внешние ссылки уже проверены и SQL-слой может выполнять запись.
    /// </summary>
    private sealed record PreparedExcelImport(
        IReadOnlyList<ImportSupplierRowViewModel> Suppliers,
        IReadOnlyList<ImportOrderRowViewModel> Orders,
        IReadOnlyList<ImportInstructionRowViewModel> Instructions,
        IReadOnlyList<ImportSchemeFileRowViewModel> SchemeFiles,
        IReadOnlyList<ImportSchemeLinkRowViewModel> SchemeLinks);
}
