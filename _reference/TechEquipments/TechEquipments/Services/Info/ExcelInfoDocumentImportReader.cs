using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace TechEquipments
{
    internal static class ExcelInfoDocumentImportReader
    {
        private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static InfoDocumentExcelImportPlan ReadSchemePlan(string excelPath)
        {
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
                throw new FileNotFoundException("Excel file not found.", excelPath);

            using var archive = ZipFile.OpenRead(excelPath);

            var sharedStrings = ReadSharedStrings(archive);
            var cells = ReadSheetCells(archive, sharedStrings, "SCHEME");

            var plan = new InfoDocumentExcelImportPlan
            {
                SheetName = "SCHEME"
            };

            ParseStationTable(cells, plan);
            ParseGroupTable(cells, plan);
            ParseEquipmentTable(cells, plan);

            return plan;
        }

        public static InfoDocumentExcelImportPlan ReadInstructionWorkbook(string excelPath)
        {
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
                throw new FileNotFoundException("Excel file not found.", excelPath);

            using var archive = ZipFile.OpenRead(excelPath);

            var sharedStrings = ReadSharedStrings(archive);

            var instructionCells = ReadSheetCells(archive, sharedStrings, "INSTRUCTION");
            var ordersCells = ReadSheetCells(archive, sharedStrings, "ORDERS");
            var supplierCells = ReadSheetCells(archive, sharedStrings, "SUPPLIER");

            var instructionBaseFolder = GetCell(instructionCells, "B1").Trim();

            if (string.IsNullOrWhiteSpace(instructionBaseFolder))
                throw new InvalidOperationException("INSTRUCTION!B1 must contain instruction files base folder.");

            var plan = new InfoDocumentExcelImportPlan
            {
                SheetName = "INSTRUCTION",
                InstructionBaseFolder = instructionBaseFolder,
                InstructionImagesFolder = Path.Combine(instructionBaseFolder, "Images"),
                SupplierLogoFolder = Path.Combine(instructionBaseFolder, "Supplier_logo")
            };

            ParseInstructionEquipmentTable(instructionCells, plan);
            ParseOrdersTable(ordersCells, plan);
            ParseSupplierTable(supplierCells, plan);

            return plan;
        }

        private static void ParseStationTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            var baseFolder = GetCell(cells, "A1").Trim();

            EnsureHeader(cells, "A3", "Station");
            EnsureHeader(cells, "B3", "Source");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var station = GetCell(cells, $"A{row}").Trim();
                var sourceText = GetCell(cells, $"B{row}").Trim();

                if (string.IsNullOrWhiteSpace(station) &&
                    string.IsNullOrWhiteSpace(sourceText))
                    continue;

                var item = new StationSourceRow
                {
                    RowNumber = row,
                    BaseFolder = baseFolder,
                    Station = station
                };

                foreach (var source in SplitCsv(sourceText))
                    item.Sources.Add(source);

                plan.StationRows.Add(item);
            }
        }

        private static void ParseGroupTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            var baseFolder = GetCell(cells, "D1").Trim();

            EnsureHeader(cells, "D3", "Group");
            EnsureHeader(cells, "E3", "Source");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var groupText = GetCell(cells, $"D{row}").Trim();
                var sourceText = GetCell(cells, $"E{row}").Trim();

                if (string.IsNullOrWhiteSpace(groupText) &&
                    string.IsNullOrWhiteSpace(sourceText))
                    continue;

                var item = new GroupSourceRow
                {
                    RowNumber = row,
                    BaseFolder = baseFolder
                };

                foreach (var group in SplitCsv(groupText))
                    item.Groups.Add(group);

                foreach (var source in SplitCsv(sourceText))
                    item.Sources.Add(source);

                plan.GroupRows.Add(item);
            }
        }

        private static void ParseEquipmentTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            var baseFolder = GetCell(cells, "G1").Trim();

            EnsureHeader(cells, "G3", "Equipment");
            EnsureHeader(cells, "H3", "Source");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var equipmentText = GetCell(cells, $"G{row}").Trim();
                var sourceText = GetCell(cells, $"H{row}").Trim();

                if (string.IsNullOrWhiteSpace(equipmentText) &&
                    string.IsNullOrWhiteSpace(sourceText))
                    continue;

                var item = new EquipmentSourceRow
                {
                    RowNumber = row,
                    BaseFolder = baseFolder
                };

                foreach (var equipment in SplitCsv(equipmentText))
                    item.Equipments.Add(equipment);

                foreach (var source in SplitCsv(sourceText))
                    item.Sources.Add(source);

                plan.EquipmentRows.Add(item);
            }
        }

        private static void ParseInstructionEquipmentTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            EnsureHeader(cells, "B3", "Station");
            EnsureHeader(cells, "C3", "Type");
            EnsureHeader(cells, "D3", "Equipment");
            EnsureHeader(cells, "E3", "Product code");
            EnsureHeader(cells, "F3", "Supplier");
            EnsureHeader(cells, "G3", "Description");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var station = GetCell(cells, $"B{row}").Trim();
                var type = GetCell(cells, $"C{row}").Trim();
                var equipment = GetCell(cells, $"D{row}").Trim();
                var productCode = GetCell(cells, $"E{row}").Trim();
                var supplier = GetCell(cells, $"F{row}").Trim();
                var description = GetCell(cells, $"G{row}").Trim();

                if (string.IsNullOrWhiteSpace(station) &&
                    string.IsNullOrWhiteSpace(type) &&
                    string.IsNullOrWhiteSpace(equipment) &&
                    string.IsNullOrWhiteSpace(productCode))
                {
                    continue;
                }

                plan.InstructionEquipmentRows.Add(new InstructionEquipmentRow
                {
                    RowNumber = row,
                    Station = station,
                    Type = type,
                    Equipment = equipment,
                    ProductCode = productCode,
                    Supplier = supplier,
                    Description = description
                });
            }
        }

        private static void ParseOrdersTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            EnsureHeader(cells, "B2", "Type");
            EnsureHeader(cells, "C2", "Product code");
            EnsureHeader(cells, "D2", "Supplier");
            EnsureHeader(cells, "E2", "Source");
            EnsureHeader(cells, "F2", "Description");
            EnsureHeader(cells, "G2", "Image");

            var maxRow = GetMaxRow(cells);

            for (var row = 3; row <= maxRow; row++)
            {
                var type = GetCell(cells, $"B{row}").Trim();
                var productCode = GetCell(cells, $"C{row}").Trim();
                var supplier = GetCell(cells, $"D{row}").Trim();
                var source = GetCell(cells, $"E{row}").Trim();
                var description = GetCell(cells, $"F{row}").Trim();
                var image = GetCell(cells, $"G{row}").Trim();

                if (string.IsNullOrWhiteSpace(type) &&
                    string.IsNullOrWhiteSpace(productCode) &&
                    string.IsNullOrWhiteSpace(supplier) &&
                    string.IsNullOrWhiteSpace(source) &&
                    string.IsNullOrWhiteSpace(description) &&
                    string.IsNullOrWhiteSpace(image))
                {
                    continue;
                }

                var item = new InstructionOrderRow
                {
                    RowNumber = row,
                    Type = type,
                    ProductCode = productCode,
                    Supplier = supplier,
                    Source = source,
                    Description = description,
                    Image = image
                };

                foreach (var part in SplitCsv(source))
                    item.Sources.Add(part);

                foreach (var part in SplitCsv(image))
                    item.Images.Add(part);

                plan.InstructionOrderRows.Add(item);
            }
        }

        private static void ParseSupplierTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            EnsureHeader(cells, "B2", "Supplier");
            EnsureHeader(cells, "C2", "Supplier_logo");

            var maxRow = GetMaxRow(cells);

            for (var row = 3; row <= maxRow; row++)
            {
                var supplier = GetCell(cells, $"B{row}").Trim();
                var logo = GetCell(cells, $"C{row}").Trim();

                if (string.IsNullOrWhiteSpace(supplier) &&
                    string.IsNullOrWhiteSpace(logo))
                {
                    continue;
                }

                plan.InstructionSupplierRows.Add(new InstructionSupplierRow
                {
                    RowNumber = row,
                    Supplier = supplier,
                    SupplierLogo = logo,
                    SupplierLogoPath = ResolveSourcePath(plan.SupplierLogoFolder, logo)
                });
            }
        }

        private static Dictionary<string, string> ReadSheetCells(
            ZipArchive archive,
            Dictionary<string, string> sharedStrings,
            string sheetName)
        {
            var sheetPath = ResolveSheetPath(archive, sheetName);

            var sheetEntry = archive.GetEntry(sheetPath)
                ?? throw new InvalidOperationException($"Sheet XML was not found: {sheetPath}");

            XDocument sheetDoc;

            using (var stream = sheetEntry.Open())
                sheetDoc = XDocument.Load(stream);

            return ReadCells(sheetDoc, sharedStrings);
        }

        private static Dictionary<string, string> ReadSharedStrings(ZipArchive archive)
        {
            var result = new Dictionary<string, string>();

            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return result;

            XDocument doc;

            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            var index = 0;

            foreach (var si in doc.Descendants(MainNs + "si"))
            {
                var text = string.Concat(si.Descendants(MainNs + "t").Select(t => t.Value));
                result[index.ToString(CultureInfo.InvariantCulture)] = text;
                index++;
            }

            return result;
        }

        private static string ResolveSheetPath(ZipArchive archive, string sheetName)
        {
            var workbookEntry = archive.GetEntry("xl/workbook.xml")
                ?? throw new InvalidOperationException("xl/workbook.xml was not found.");

            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
                ?? throw new InvalidOperationException("xl/_rels/workbook.xml.rels was not found.");

            XDocument workbookDoc;
            XDocument relsDoc;

            using (var stream = workbookEntry.Open())
                workbookDoc = XDocument.Load(stream);

            using (var stream = relsEntry.Open())
                relsDoc = XDocument.Load(stream);

            var sheet = workbookDoc
                .Descendants(MainNs + "sheet")
                .FirstOrDefault(x => string.Equals(
                    (string?)x.Attribute("name"),
                    sheetName,
                    StringComparison.OrdinalIgnoreCase));

            if (sheet == null)
                throw new InvalidOperationException($"Sheet '{sheetName}' was not found.");

            var relId = (string?)sheet.Attribute(RelNs + "id");
            if (string.IsNullOrWhiteSpace(relId))
                throw new InvalidOperationException($"Relationship id for sheet '{sheetName}' was not found.");

            var rel = relsDoc
                .Descendants(PackageRelNs + "Relationship")
                .FirstOrDefault(x => string.Equals(
                    (string?)x.Attribute("Id"),
                    relId,
                    StringComparison.OrdinalIgnoreCase));

            var target = (string?)rel?.Attribute("Target");
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException($"Relationship target for sheet '{sheetName}' was not found.");

            target = target.Replace('\\', '/');

            if (target.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase))
                return target.TrimStart('/');

            if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                return target;

            return "xl/" + target.TrimStart('/');
        }

        private static Dictionary<string, string> ReadCells(
            XDocument sheetDoc,
            Dictionary<string, string> sharedStrings)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cell in sheetDoc.Descendants(MainNs + "c"))
            {
                var reference = ((string?)cell.Attribute("r") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(reference))
                    continue;

                var type = ((string?)cell.Attribute("t") ?? "").Trim();

                string value;

                if (type.Equals("inlineStr", StringComparison.OrdinalIgnoreCase))
                {
                    value = string.Concat(cell.Descendants(MainNs + "t").Select(x => x.Value));
                }
                else
                {
                    var raw = cell.Element(MainNs + "v")?.Value ?? "";

                    if (type.Equals("s", StringComparison.OrdinalIgnoreCase))
                    {
                        value = sharedStrings.TryGetValue(raw, out var s)
                            ? s
                            : "";
                    }
                    else
                    {
                        value = raw;
                    }
                }

                result[reference] = value;
            }

            return result;
        }

        private static void EnsureHeader(Dictionary<string, string> cells, string address, string expected)
        {
            var actual = GetCell(cells, address).Trim();

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Invalid Excel header at {address}. Expected '{expected}', actual '{actual}'.");
            }
        }

        private static string GetCell(Dictionary<string, string> cells, string address)
        {
            return cells.TryGetValue(address, out var value)
                ? value ?? ""
                : "";
        }

        private static int GetMaxRow(Dictionary<string, string> cells)
        {
            return cells.Keys
                .Select(TryGetRowIndex)
                .DefaultIfEmpty(0)
                .Max();
        }

        private static int TryGetRowIndex(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return 0;

            var digits = new string(address.Where(char.IsDigit).ToArray());

            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)
                ? row
                : 0;
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

        internal static string ResolveSourcePath(string baseFolder, string source)
        {
            source = (source ?? "").Trim();

            if (string.IsNullOrWhiteSpace(source))
                return "";

            if (Path.IsPathRooted(source))
                return Path.GetFullPath(source);

            baseFolder = (baseFolder ?? "").Trim();

            return Path.GetFullPath(Path.Combine(baseFolder, source));
        }
    }
}