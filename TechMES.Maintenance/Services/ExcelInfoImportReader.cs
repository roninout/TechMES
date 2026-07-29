using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Читает XLSX-файл импорта по контракту старого приложения TechEquipments.
/// Реализация использует Open XML внутри ZIP-контейнера напрямую и не требует установленного Excel.
/// </summary>
public sealed class ExcelInfoImportReader
{
    private static readonly XNamespace SpreadsheetNs =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly XNamespace OfficeRelationshipNs =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly XNamespace PackageRelationshipNs =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// Читает обязательные листы INSTRUCTION, ORDERS, SUPPLIER и SCHEME.
    /// При нарушении формата возвращает понятную ошибку до начала любых изменений в БД.
    /// </summary>
    public ExcelInfoImportDocument Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Excel import file was not found.", filePath);

        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import supports only the .xlsx format.");

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var sharedStrings = ReadSharedStrings(archive);
            var sheets = ReadWorksheets(archive, sharedStrings);

            var instruction = GetRequiredSheet(sheets, "INSTRUCTION");
            var orders = GetRequiredSheet(sheets, "ORDERS");
            var suppliers = GetRequiredSheet(sheets, "SUPPLIER");
            var schemes = GetRequiredSheet(sheets, "SCHEME");

            return new ExcelInfoImportDocument
            {
                InstructionRoot = instruction.Get("B1"),
                SchemeStationRoot = schemes.Get("A1"),
                SchemeGroupRoot = schemes.Get("D1"),
                SchemeEquipmentRoot = schemes.Get("G1"),
                Suppliers = ReadSuppliers(suppliers),
                Orders = ReadOrders(orders),
                Instructions = ReadInstructions(instruction),
                Schemes = ReadSchemes(schemes)
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Excel import file cannot be read: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Загружает sharedStrings.xml, через который XLSX обычно хранит текстовые значения.
    /// </summary>
    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return [];

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document
            .Descendants(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(x => x.Value)))
            .ToList();
    }

    /// <summary>
    /// Сопоставляет имена листов с XML-файлами книги и читает значения ячеек.
    /// </summary>
    private static Dictionary<string, WorksheetValues> ReadWorksheets(
        ZipArchive archive,
        IReadOnlyList<string> sharedStrings)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("XLSX does not contain xl/workbook.xml.");
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("XLSX does not contain workbook relationships.");

        XDocument workbook;
        XDocument relationships;
        using (var stream = workbookEntry.Open())
            workbook = XDocument.Load(stream);
        using (var stream = relationshipsEntry.Open())
            relationships = XDocument.Load(stream);

        var targets = relationships
            .Descendants(PackageRelationshipNs + "Relationship")
            .Where(item => item.Attribute("Id") is not null && item.Attribute("Target") is not null)
            .ToDictionary(
                item => item.Attribute("Id")!.Value,
                item => NormalizeWorksheetPath(item.Attribute("Target")!.Value),
                StringComparer.Ordinal);

        var result = new Dictionary<string, WorksheetValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Descendants(SpreadsheetNs + "sheet"))
        {
            var name = sheet.Attribute("name")?.Value?.Trim();
            var relationshipId = sheet.Attribute(OfficeRelationshipNs + "id")?.Value;
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(relationshipId)
                || !targets.TryGetValue(relationshipId, out var worksheetPath))
            {
                continue;
            }

            var entry = archive.GetEntry(worksheetPath)
                ?? throw new InvalidDataException($"Worksheet '{name}' points to missing '{worksheetPath}'.");
            result[name] = ReadWorksheet(entry, sharedStrings);
        }

        return result;
    }

    /// <summary>
    /// Приводит относительный Target relationship к пути внутри XLSX ZIP-контейнера.
    /// </summary>
    private static string NormalizeWorksheetPath(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            return normalized;

        return $"xl/{normalized.TrimStart('.', '/')}";
    }

    /// <summary>
    /// Читает одну XML-страницу и индексирует значения по адресам A1, B1 и т.д.
    /// </summary>
    private static WorksheetValues ReadWorksheet(
        ZipArchiveEntry entry,
        IReadOnlyList<string> sharedStrings)
    {
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in document.Descendants(SpreadsheetNs + "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            if (string.IsNullOrWhiteSpace(reference))
                continue;

            var type = cell.Attribute("t")?.Value;
            string value;
            if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
            {
                value = string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(x => x.Value));
            }
            else
            {
                var rawValue = cell.Element(SpreadsheetNs + "v")?.Value ?? "";
                if (string.Equals(type, "s", StringComparison.Ordinal)
                    && int.TryParse(rawValue, out var sharedIndex)
                    && sharedIndex >= 0
                    && sharedIndex < sharedStrings.Count)
                {
                    value = sharedStrings[sharedIndex];
                }
                else
                {
                    value = rawValue;
                }
            }

            cells[reference] = value.Trim();
        }

        return new WorksheetValues(cells);
    }

    private static WorksheetValues GetRequiredSheet(
        IReadOnlyDictionary<string, WorksheetValues> sheets,
        string name)
    {
        return sheets.TryGetValue(name, out var sheet)
            ? sheet
            : throw new InvalidDataException($"Required worksheet '{name}' was not found.");
    }

    private static IReadOnlyList<ExcelSupplierImportRow> ReadSuppliers(WorksheetValues sheet)
    {
        var result = new List<ExcelSupplierImportRow>();
        foreach (var row in sheet.RowsFrom(3))
        {
            var supplier = sheet.Get($"B{row}");
            var logo = sheet.Get($"C{row}");
            if (string.IsNullOrWhiteSpace(supplier) && string.IsNullOrWhiteSpace(logo))
                continue;

            if (string.IsNullOrWhiteSpace(supplier))
                throw new InvalidDataException($"SUPPLIER row {row}: Supplier is empty.");

            result.Add(new ExcelSupplierImportRow(supplier, logo));
        }

        return result;
    }

    private static IReadOnlyList<ExcelOrderImportRow> ReadOrders(WorksheetValues sheet)
    {
        var result = new List<ExcelOrderImportRow>();
        foreach (var row in sheet.RowsFrom(3))
        {
            var type = sheet.Get($"B{row}");
            var productCode = sheet.Get($"C{row}");
            var supplier = sheet.Get($"D{row}");
            var source = sheet.Get($"E{row}");
            var description = sheet.Get($"F{row}");
            var image = sheet.Get($"G{row}");
            if (new[] { type, productCode, supplier, source, description, image }.All(string.IsNullOrWhiteSpace))
                continue;

            result.Add(new ExcelOrderImportRow(type, productCode, supplier, source, description, image));
        }

        return result;
    }

    private static IReadOnlyList<ExcelInstructionImportRow> ReadInstructions(WorksheetValues sheet)
    {
        var result = new List<ExcelInstructionImportRow>();
        foreach (var row in sheet.RowsFrom(4))
        {
            var station = sheet.Get($"B{row}");
            var type = sheet.Get($"C{row}");
            var equipment = sheet.Get($"D{row}");
            var productCode = sheet.Get($"E{row}");
            var supplier = sheet.Get($"F{row}");
            var description = sheet.Get($"G{row}");
            if (new[] { station, type, equipment, productCode, supplier, description }.All(string.IsNullOrWhiteSpace))
                continue;

            result.Add(new ExcelInstructionImportRow(
                station,
                type,
                equipment,
                productCode,
                supplier,
                description));
        }

        return result;
    }

    private static IReadOnlyList<ExcelSchemeImportRow> ReadSchemes(WorksheetValues sheet)
    {
        var result = new List<ExcelSchemeImportRow>();
        ReadSchemeBlock(result, sheet, ExcelSchemeScope.Station, "A", "B", sheet.Get("A1"));
        ReadSchemeBlock(result, sheet, ExcelSchemeScope.Group, "D", "E", sheet.Get("D1"));
        ReadSchemeBlock(result, sheet, ExcelSchemeScope.Equipment, "G", "H", sheet.Get("G1"));
        return result;
    }

    private static void ReadSchemeBlock(
        ICollection<ExcelSchemeImportRow> result,
        WorksheetValues sheet,
        ExcelSchemeScope scope,
        string targetColumn,
        string sourceColumn,
        string sourceRoot)
    {
        foreach (var row in sheet.RowsFrom(4))
        {
            var target = sheet.Get($"{targetColumn}{row}");
            var source = sheet.Get($"{sourceColumn}{row}");

            /*
             * Заполненная станция, группа или единица оборудования при пустом
             * Source означает, что схема для этого объекта пока не назначена.
             * Такая строка является допустимой и не должна останавливать весь
             * импорт. Она просто не создаёт файл схемы и связь с оборудованием.
             */
            if (string.IsNullOrWhiteSpace(source))
                continue;

            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException($"SCHEME row {row}: source is specified, but target is empty.");

            result.Add(new ExcelSchemeImportRow(scope, target, sourceRoot, source));
        }
    }

    /// <summary>
    /// Компактное представление листа с определением последней используемой строки.
    /// </summary>
    private sealed class WorksheetValues(IReadOnlyDictionary<string, string> cells)
    {
        public string Get(string address)
            => cells.TryGetValue(address, out var value) ? value.Trim() : "";

        public IEnumerable<int> RowsFrom(int firstRow)
        {
            var lastRow = cells.Keys
                .Select(GetRowNumber)
                .DefaultIfEmpty(firstRow - 1)
                .Max();

            return Enumerable.Range(firstRow, Math.Max(0, lastRow - firstRow + 1));
        }

        private static int GetRowNumber(string address)
        {
            var digits = new string(address.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var row) ? row : 0;
        }
    }
}
