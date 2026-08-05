using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Создаёт XLSX-файл, полностью совместимый с ExcelInfoImportReader.
///
/// Реализация использует только стандартные ZIP/XML API .NET
/// и не требует установленного Microsoft Excel.
/// </summary>
public sealed class ExcelInfoExportWriter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    // До этой строки выпадающие списки ORDERS остаются доступными
    // для добавления новых заказов вручную.
    private const int EditableOrderLastRow = 10000;

    /// <summary>
    /// Асинхронно создаёт Excel-книгу.
    /// </summary>
    public Task WriteAsync(string filePath, InfoExportWorkbookData data, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Write(filePath, data, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Создаёт валидный XLSX ZIP-контейнер с формулами,
    /// выпадающими списками и листом TYPE.
    /// </summary>
    private static void Write(string filePath, InfoExportWorkbookData data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(filePath))
            File.Delete(filePath);

        var types = BuildTypeList(data);

        using var fileStream = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        WriteContentTypes(archive);
        WriteRootRelationships(archive);
        WriteWorkbook(archive);
        WriteWorkbookRelationships(archive);
        WriteStyles(archive);

        WriteSupplierSheet(archive, data.Suppliers, cancellationToken);
        WriteOrdersSheet(archive, data.Orders, cancellationToken);
        WriteInstructionSheet(archive, data, cancellationToken);
        WriteSchemeSheet(archive, data, cancellationToken);
        WriteTypeSheet(archive, types, cancellationToken);
    }

    /// <summary>
    /// Записывает список частей XLSX.
    /// </summary>
    private static void WriteContentTypes(ZipArchive archive)
    {
        const string content = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>

          <Override PartName="/xl/workbook.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>

          <Override PartName="/xl/styles.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>

          <Override PartName="/xl/worksheets/sheet1.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>

          <Override PartName="/xl/worksheets/sheet2.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>

          <Override PartName="/xl/worksheets/sheet3.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>

          <Override PartName="/xl/worksheets/sheet4.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>

          <Override PartName="/xl/worksheets/sheet5.xml"
                    ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

        WriteTextEntry(archive, "[Content_Types].xml", content);
    }

    /// <summary>
    /// Связывает пакет с workbook.xml.
    /// </summary>
    private static void WriteRootRelationships(ZipArchive archive)
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1"
                            Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"
                            Target="xl/workbook.xml"/>
            </Relationships>
            """;

        WriteTextEntry(archive, "_rels/.rels", content);
    }

    /// <summary>
    /// Создаёт книгу с четырьмя импортными листами и служебным листом TYPE.
    ///
    /// Именованные диапазоны используются выпадающими списками и формулами.
    /// Они автоматически расширяются при добавлении новых строк.
    /// </summary>
    private static void WriteWorkbook(ZipArchive archive)
    {
        const string content = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">

          <bookViews>
            <workbookView activeTab="0"/>
          </bookViews>

          <sheets>
            <sheet name="SUPPLIER" sheetId="1" r:id="rId1"/>
            <sheet name="ORDERS" sheetId="2" r:id="rId2"/>
            <sheet name="INSTRUCTION" sheetId="3" r:id="rId3"/>
            <sheet name="SCHEME" sheetId="4" r:id="rId4"/>
            <sheet name="TYPE" sheetId="5" r:id="rId5"/>
          </sheets>

          <definedNames>
            <definedName name="TechMES_TypeList">
              OFFSET('TYPE'!$B$3,0,0,MAX(1,COUNTA('TYPE'!$B:$B)-1),1)
            </definedName>

            <definedName name="TechMES_SupplierList">
              OFFSET('SUPPLIER'!$B$3,0,0,MAX(1,COUNTA('SUPPLIER'!$B:$B)-1),1)
            </definedName>

            <definedName name="TechMES_ProductCodeList">
              OFFSET('ORDERS'!$C$3,0,0,MAX(1,COUNTA('ORDERS'!$C:$C)-1),1)
            </definedName>

            <definedName name="TechMES_OrderLookup">
              OFFSET('ORDERS'!$C$3,0,0,MAX(1,COUNTA('ORDERS'!$C:$C)-1),4)
            </definedName>
          </definedNames>

          <calcPr calcId="191029"
                  calcMode="auto"
                  fullCalcOnLoad="1"
                  forceFullCalc="1"/>
        </workbook>
        """;

        WriteTextEntry(archive, "xl/workbook.xml", content);
    }

    /// <summary>
    /// Связывает workbook с пятью листами и стилями.
    /// </summary>
    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        const string content = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">

          <Relationship Id="rId1"
                        Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"
                        Target="worksheets/sheet1.xml"/>

          <Relationship Id="rId2"
                        Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"
                        Target="worksheets/sheet2.xml"/>

          <Relationship Id="rId3"
                        Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"
                        Target="worksheets/sheet3.xml"/>

          <Relationship Id="rId4"
                        Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"
                        Target="worksheets/sheet4.xml"/>

          <Relationship Id="rId5"
                        Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"
                        Target="worksheets/sheet5.xml"/>

          <Relationship Id="rId6"
                        Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"
                        Target="styles.xml"/>
        </Relationships>
        """;

        WriteTextEntry(archive, "xl/_rels/workbook.xml.rels", content);
    }

    /// <summary>
    /// Создаёт базовые стили заголовка и служебной строки.
    /// </summary>
    private static void WriteStyles(ZipArchive archive)
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="3">
                <font>
                  <sz val="11"/>
                  <name val="Calibri"/>
                  <family val="2"/>
                </font>
                <font>
                  <b/>
                  <color rgb="FFFFFFFF"/>
                  <sz val="11"/>
                  <name val="Calibri"/>
                  <family val="2"/>
                </font>
                <font>
                  <b/>
                  <sz val="11"/>
                  <name val="Calibri"/>
                  <family val="2"/>
                </font>
              </fonts>

              <fills count="3">
                <fill>
                  <patternFill patternType="none"/>
                </fill>
                <fill>
                  <patternFill patternType="gray125"/>
                </fill>
                <fill>
                  <patternFill patternType="solid">
                    <fgColor rgb="FF1F4E78"/>
                    <bgColor indexed="64"/>
                  </patternFill>
                </fill>
              </fills>

              <borders count="1">
                <border>
                  <left/>
                  <right/>
                  <top/>
                  <bottom/>
                  <diagonal/>
                </border>
              </borders>

              <cellStyleXfs count="1">
                <xf numFmtId="0"
                    fontId="0"
                    fillId="0"
                    borderId="0"/>
              </cellStyleXfs>

              <cellXfs count="3">
                <xf numFmtId="0"
                    fontId="0"
                    fillId="0"
                    borderId="0"
                    xfId="0"/>

                <xf numFmtId="0"
                    fontId="1"
                    fillId="2"
                    borderId="0"
                    xfId="0"
                    applyFont="1"
                    applyFill="1"
                    applyAlignment="1">
                  <alignment horizontal="center"
                             vertical="center"/>
                </xf>

                <xf numFmtId="0"
                    fontId="2"
                    fillId="0"
                    borderId="0"
                    xfId="0"
                    applyFont="1"/>
              </cellXfs>

              <cellStyles count="1">
                <cellStyle name="Normal"
                           xfId="0"
                           builtinId="0"/>
              </cellStyles>

              <dxfs count="0"/>

              <tableStyles count="0"
                           defaultTableStyle="TableStyleMedium2"
                           defaultPivotStyle="PivotStyleLight16"/>
            </styleSheet>
            """;

        WriteTextEntry(archive, "xl/styles.xml", content);
    }

    /// <summary>
    /// Формирует значения листа TYPE.
    ///
    /// Сначала добавляются стандартные WEB-типы, затем дополнительные
    /// фактические значения из ORDERS и INSTRUCTION.
    /// </summary>
    private static IReadOnlyList<string> BuildTypeList(InfoExportWorkbookData data)
    {
        var standardTypes = new[]
        {
        "AI",
        "DI",
        "DO",
        "Motor",
        "ATV",
        "VGA",
        "VGD",
        "VGA_EL",
        "Equipment"
    };

        var actualTypes = data.Orders
            .Select(x => x.Type)
            .Concat(data.Instructions.Select(x => x.Type))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        return standardTypes
            .Concat(actualTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Создаёт служебный лист TYPE, используемый выпадающими списками.
    /// </summary>
    private static void WriteTypeSheet(ZipArchive archive, IReadOnlyList<string> types, CancellationToken cancellationToken)
    {
        var rows = new List<WorksheetRow>
    {
        new(1,
        [
            Cell("A", 1, "TechMES Info export", 2)
        ]),

        new(2,
        [
            Cell("A", 2, "№", 1),
            Cell("B", 2, "Type", 1)
        ])
    };

        for (var index = 0; index < types.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowNumber = index + 3;

            rows.Add(new WorksheetRow(rowNumber,
            [
                Cell("A", rowNumber, (index + 1).ToString()),
            Cell("B", rowNumber, types[index])
            ]));
        }

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet5.xml",
            [7, 24],
            rows,
            frozenRows: 2,
            autoFilterReference: $"B2:B{Math.Max(2, types.Count + 2)}",
            cancellationToken);
    }

    /// <summary>
    /// Создаёт лист SUPPLIER.
    /// Данные начинаются с третьей строки, как ожидает импорт.
    /// </summary>
    private static void WriteSupplierSheet(ZipArchive archive, IReadOnlyList<InfoExportWorkbookSupplierRow> suppliers, CancellationToken cancellationToken)
    {
        var rows =
            new List<WorksheetRow>
            {
                new(
                    1,
                    [
                        Cell("A", 1, "TechMES Info export", 2)
                    ]),

                new(
                    2,
                    [
                        Cell("A", 2, "№", 1),
                        Cell("B", 2, "Supplier", 1),
                        Cell("C", 2, "Supplier_logo", 1)
                    ])
            };

        for (var index = 0;
             index < suppliers.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowNumber =
                index + 3;

            var item =
                suppliers[index];

            rows.Add(
                new WorksheetRow(
                    rowNumber,
                    [
                        Cell("A", rowNumber, (index + 1).ToString()),
                        Cell("B", rowNumber, item.Supplier),
                        Cell("C", rowNumber, item.Logo)
                    ]));
        }

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet1.xml",
            [7, 34, 60],
            rows,
            frozenRows: 2,
            autoFilterReference:
                $"B2:C{Math.Max(2, suppliers.Count + 2)}",
            cancellationToken);
    }

    /// <summary>
    /// Создаёт лист ORDERS.
    ///
    /// Type и Supplier имеют выпадающие списки.
    /// Проверка распространяется до строки 10000, поэтому новые заказы
    /// можно добавлять вручную без копирования настроек ячеек.
    /// </summary>
    private static void WriteOrdersSheet(ZipArchive archive, IReadOnlyList<InfoExportWorkbookOrderRow> orders, CancellationToken cancellationToken)
    {
        var rows = new List<WorksheetRow>
    {
        new(1,
        [
            Cell("A", 1, "TechMES Info export", 2)
        ]),

        new(2,
        [
            Cell("A", 2, "№", 1),
            Cell("B", 2, "Type", 1),
            Cell("C", 2, "Product code", 1),
            Cell("D", 2, "Supplier", 1),
            Cell("E", 2, "Source", 1),
            Cell("F", 2, "Description", 1),
            Cell("G", 2, "Image", 1)
        ])
    };

        for (var index = 0; index < orders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowNumber = index + 3;
            var item = orders[index];

            rows.Add(new WorksheetRow(rowNumber,
            [
                Cell("A", rowNumber, (index + 1).ToString()),
            Cell("B", rowNumber, item.Type),
            Cell("C", rowNumber, item.ProductCode),
            Cell("D", rowNumber, item.Supplier),
            Cell("E", rowNumber, item.Source),
            Cell("F", rowNumber, item.Description),
            Cell("G", rowNumber, item.Image)
            ]));
        }

        var lastValidationRow = Math.Max(EditableOrderLastRow, orders.Count + 2);

        IReadOnlyList<WorksheetDataValidation> dataValidations =
        [
            new($"B3:B{lastValidationRow}", "TechMES_TypeList"),
        new($"D3:D{lastValidationRow}", "TechMES_SupplierList")
        ];

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet2.xml",
            [7, 18, 28, 28, 60, 90, 60],
            rows,
            frozenRows: 2,
            autoFilterReference: $"B2:G{Math.Max(2, orders.Count + 2)}",
            cancellationToken,
            dataValidations);
    }

    /// <summary>
    /// Создаёт лист INSTRUCTION.
    ///
    /// Station вычисляется из Equipment.
    /// Type выбирается из TYPE.
    /// Product code выбирается из ORDERS.
    /// Supplier и Description автоматически подтягиваются по Product code.
    ///
    /// Формулы Supplier и Description создаются для каждой строки,
    /// включая строки, у которых Product code пока не выбран.
    ///
    /// Каждая формула содержит сохранённый результат, поэтому файл
    /// можно сразу импортировать обратно, не открывая его в Excel.
    /// </summary>
    private static void WriteInstructionSheet(ZipArchive archive, InfoExportWorkbookData data, CancellationToken cancellationToken)
    {
        var ordersByProductCode = data.Orders
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
            .GroupBy(x => x.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<WorksheetRow>
    {
        new(1,
        [
            Cell("A", 1, "Instruction root", 2),
            Cell("B", 1, data.InstructionRoot, 2)
        ]),

        new(2,
        [
            Cell("A", 2, "TechMES Info export", 2)
        ]),

        new(3,
        [
            Cell("A", 3, "№", 1),
            Cell("B", 3, "Station", 1),
            Cell("C", 3, "Type", 1),
            Cell("D", 3, "Equipment", 1),
            Cell("E", 3, "Product code", 1),
            Cell("F", 3, "Supplier", 1),
            Cell("G", 3, "Description", 1)
        ])
    };

        for (var index = 0; index < data.Instructions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowNumber = index + 4;
            var item = data.Instructions[index];

            var stationFormula = $"IFERROR(LEFT(D{rowNumber},FIND(\".\",D{rowNumber})-1),\"\")";
            var supplierFormula = $"IF($E{rowNumber}=\"\",\"\",IFERROR(VLOOKUP($E{rowNumber},TechMES_OrderLookup,2,FALSE),\"\"))";
            var descriptionFormula = $"IF($E{rowNumber}=\"\",\"\",IFERROR(VLOOKUP($E{rowNumber},TechMES_OrderLookup,4,FALSE),\"\"))";

            /*
             * Cached values нужны для импорта без предварительного
             * открытия и сохранения файла в Excel.
             *
             * Если Product code найден в ORDERS, сохраняем результат,
             * который должна вернуть формула.
             *
             * Если код пустой или отсутствует в ORDERS, сохраняем текущие
             * значения equip_info, чтобы экспорт не потерял данные.
             */
            var cachedSupplier = item.Supplier;
            var cachedDescription = item.Description;

            if (!string.IsNullOrWhiteSpace(item.ProductCode)
                && ordersByProductCode.TryGetValue(item.ProductCode.Trim(), out var order))
            {
                cachedSupplier = order.Supplier;
                cachedDescription = order.Description;
            }

            rows.Add(new WorksheetRow(rowNumber,
            [
                Cell("A", rowNumber, (index + 1).ToString()),
                FormulaCell("B", rowNumber, stationFormula, item.Station),
                Cell("C", rowNumber, item.Type),
                Cell("D", rowNumber, item.Equipment),
                Cell("E", rowNumber, item.ProductCode),
                FormulaCell("F", rowNumber, supplierFormula, cachedSupplier),
                FormulaCell("G", rowNumber, descriptionFormula, cachedDescription)
                ]));
        }

        var lastInstructionRow = Math.Max(4, data.Instructions.Count + 3);

        IReadOnlyList<WorksheetDataValidation> dataValidations =
        [
            new($"C4:C{lastInstructionRow}", "TechMES_TypeList"),
        new($"E4:E{lastInstructionRow}", "TechMES_ProductCodeList")
        ];

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet3.xml",
            [7, 14, 18, 42, 28, 28, 90],
            rows,
            frozenRows: 3,
            autoFilterReference: $"B3:G{Math.Max(3, data.Instructions.Count + 3)}",
            cancellationToken,
            dataValidations);
    }

    /// <summary>
    /// Создаёт лист SCHEME с тремя параллельными блоками:
    /// Station, Group и Equipment.
    /// </summary>
    private static void WriteSchemeSheet(ZipArchive archive, InfoExportWorkbookData data, CancellationToken cancellationToken)
    {
        var stations = data.Schemes
            .Where(x =>
                x.Scope ==
                InfoExportSchemeScope.Station)
            .OrderBy(
                x => x.Target,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                x => x.Source,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = data.Schemes
            .Where(x =>
                x.Scope ==
                InfoExportSchemeScope.Group)
            .OrderBy(
                x => x.Target,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                x => x.Source,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        var equipments = data.Schemes
            .Where(x =>
                x.Scope ==
                InfoExportSchemeScope.Equipment)
            .OrderBy(
                x => x.Target,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                x => x.Source,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows =
            new List<WorksheetRow>
            {
                new(
                    1,
                    [
                        Cell("A", 1, data.SchemeStationRoot, 2),
                        Cell("D", 1, data.SchemeGroupRoot, 2),
                        Cell("G", 1, data.SchemeEquipmentRoot, 2)
                    ]),

                new(
                    2,
                    [
                        Cell("A", 2, "Station schemes", 2),
                        Cell("D", 2, "Group schemes", 2),
                        Cell("G", 2, "Equipment schemes", 2)
                    ]),

                new(
                    3,
                    [
                        Cell("A", 3, "Station", 1),
                        Cell("B", 3, "Source", 1),
                        Cell("D", 3, "Group", 1),
                        Cell("E", 3, "Source", 1),
                        Cell("G", 3, "Equipment", 1),
                        Cell("H", 3, "Source", 1)
                    ])
            };

        var rowCount =
            Math.Max(
                stations.Count,
                Math.Max(
                    groups.Count,
                    equipments.Count));

        for (var index = 0;
             index < rowCount;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowNumber =
                index + 4;

            var cells =
                new List<WorksheetCell>();

            if (index < stations.Count)
            {
                cells.Add(
                    Cell(
                        "A",
                        rowNumber,
                        stations[index].Target));

                cells.Add(
                    Cell(
                        "B",
                        rowNumber,
                        stations[index].Source));
            }

            if (index < groups.Count)
            {
                cells.Add(
                    Cell(
                        "D",
                        rowNumber,
                        groups[index].Target));

                cells.Add(
                    Cell(
                        "E",
                        rowNumber,
                        groups[index].Source));
            }

            if (index < equipments.Count)
            {
                cells.Add(
                    Cell(
                        "G",
                        rowNumber,
                        equipments[index].Target));

                cells.Add(
                    Cell(
                        "H",
                        rowNumber,
                        equipments[index].Source));
            }

            rows.Add(
                new WorksheetRow(
                    rowNumber,
                    cells));
        }

        WriteWorksheet(
            archive,
            "xl/worksheets/sheet4.xml",
            [28, 60, 4, 36, 60, 4, 48, 60],
            rows,
            frozenRows: 3,
            autoFilterReference: null,
            cancellationToken);
    }

    /// <summary>
    /// Записывает общий XML worksheet.
    ///
    /// Data Validation создаёт стандартные Excel-выпадающие списки.
    /// </summary>
    private static void WriteWorksheet(ZipArchive archive, string entryPath, IReadOnlyList<double> columnWidths, IReadOnlyList<WorksheetRow> rows, int frozenRows, string? autoFilterReference, CancellationToken cancellationToken, IReadOnlyList<WorksheetDataValidation>? dataValidations = null)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false
        };

        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);

        writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
        writer.WriteStartElement("sheetView", SpreadsheetNamespace);
        writer.WriteAttributeString("workbookViewId", "0");

        if (frozenRows > 0)
        {
            writer.WriteStartElement("pane", SpreadsheetNamespace);
            writer.WriteAttributeString("ySplit", frozenRows.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("topLeftCell", $"A{frozenRows + 1}");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr", SpreadsheetNamespace);
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteEndElement();

        writer.WriteStartElement("cols", SpreadsheetNamespace);

        for (var index = 0; index < columnWidths.Count; index++)
        {
            var columnNumber = index + 1;

            writer.WriteStartElement("col", SpreadsheetNamespace);
            writer.WriteAttributeString("min", columnNumber.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", columnNumber.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", columnWidths[index].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("sheetData", SpreadsheetNamespace);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", row.Index.ToString(CultureInfo.InvariantCulture));

            foreach (var cell in row.Cells)
                WriteCell(writer, cell);

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        if (!string.IsNullOrWhiteSpace(autoFilterReference))
        {
            writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", autoFilterReference);
            writer.WriteEndElement();
        }

        if (dataValidations is { Count: > 0 })
        {
            writer.WriteStartElement("dataValidations", SpreadsheetNamespace);
            writer.WriteAttributeString("count", dataValidations.Count.ToString(CultureInfo.InvariantCulture));

            foreach (var validation in dataValidations)
            {
                writer.WriteStartElement("dataValidation", SpreadsheetNamespace);
                writer.WriteAttributeString("type", "list");
                writer.WriteAttributeString("allowBlank", "1");
                writer.WriteAttributeString("showInputMessage", "1");
                writer.WriteAttributeString("showErrorMessage", "1");
                writer.WriteAttributeString("errorStyle", "stop");
                writer.WriteAttributeString("sqref", validation.SqRef);

                writer.WriteStartElement("formula1", SpreadsheetNamespace);
                writer.WriteString(validation.Formula1);
                writer.WriteEndElement();

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("pageMargins", SpreadsheetNamespace);
        writer.WriteAttributeString("left", "0.7");
        writer.WriteAttributeString("right", "0.7");
        writer.WriteAttributeString("top", "0.75");
        writer.WriteAttributeString("bottom", "0.75");
        writer.WriteAttributeString("header", "0.3");
        writer.WriteAttributeString("footer", "0.3");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// Записывает обычную текстовую ячейку или формулу
    /// с сохранённым текстовым результатом.
    /// </summary>
    private static void WriteCell(XmlWriter writer, WorksheetCell cell)
    {
        var value = NormalizeCellText(cell.Value);
        var formula = NormalizeCellText(cell.Formula);

        if (value.Length > 32767)
            throw new InvalidOperationException($"Excel cell {cell.Reference} contains more than 32767 characters.");

        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", cell.Reference);

        if (cell.StyleIndex > 0)
            writer.WriteAttributeString("s", cell.StyleIndex.ToString(CultureInfo.InvariantCulture));

        /*
         * Формула хранится вместе с cached value.
         * Excel пересчитает её при открытии, а ExcelInfoImportReader
         * сможет прочитать cached value сразу.
         */
        if (!string.IsNullOrWhiteSpace(formula))
        {
            writer.WriteAttributeString("t", "str");

            writer.WriteStartElement("f", SpreadsheetNamespace);
            writer.WriteString(formula);
            writer.WriteEndElement();

            writer.WriteStartElement("v", SpreadsheetNamespace);
            writer.WriteString(value);
            writer.WriteEndElement();

            writer.WriteEndElement();
            return;
        }

        writer.WriteAttributeString("t", "inlineStr");

        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteStartElement("t", SpreadsheetNamespace);
        writer.WriteAttributeString("xml", "space", XmlNamespace, "preserve");
        writer.WriteString(value);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// Удаляет недопустимые для XML 1.0 управляющие символы.
    /// </summary>
    private static string NormalizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return new string(value.Where(XmlConvert.IsXmlChar).ToArray());
    }

    private static WorksheetCell Cell(string column, int row, string? value, int styleIndex = 0)
    {
        return new WorksheetCell($"{column}{row}", value ?? "", styleIndex, null);
    }

    /// <summary>
    /// Создаёт ячейку с формулой и сохранённым результатом.
    /// Формула передаётся без начального знака "=".
    /// </summary>
    private static WorksheetCell FormulaCell(string column, int row, string formula, string? cachedValue, int styleIndex = 0)
    {
        return new WorksheetCell($"{column}{row}", cachedValue ?? "", styleIndex, formula);
    }

    /// <summary>
    /// Создаёт обычную текстовую запись в ZIP.
    /// </summary>
    private static void WriteTextEntry(ZipArchive archive, string entryPath, string content)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        writer.Write(content);
    }

    private sealed record WorksheetRow(int Index, IReadOnlyList<WorksheetCell> Cells);

    private sealed record WorksheetCell(string Reference, string Value, int StyleIndex, string? Formula);

    private sealed record WorksheetDataValidation(string SqRef, string Formula1);
}