namespace TechMES.Maintenance.Models;

/// <summary>
/// Полный снимок данных Info-модуля, прочитанный непосредственно из PostgreSQL.
/// </summary>
public sealed record InfoExportDatabaseSnapshot(
    IReadOnlyList<InfoExportSupplierRow> Suppliers,
    IReadOnlyList<InfoExportOrderRow> Orders,
    IReadOnlyList<InfoExportEquipmentInfoRow> EquipmentInfo,
    IReadOnlyList<InfoExportBinaryFile> InstructionFiles,
    IReadOnlyList<InfoExportBinaryFile> PhotoFiles,
    IReadOnlyList<InfoExportSchemeFile> SchemeFiles);

/// <summary>
/// Строка public.equip_supplier вместе с бинарным логотипом.
/// </summary>
public sealed record InfoExportSupplierRow(
    string Supplier,
    string LogoFileName,
    string LogoFileHash,
    byte[]? LogoData);

/// <summary>
/// Строка public.equip_order.
/// </summary>
public sealed record InfoExportOrderRow(
    string Type,
    string ProductCode,
    string Supplier,
    string Source,
    string Description,
    string Image);

/// <summary>
/// Строка public.equip_info.
///
/// Флаги связей позволяют отличить реальные строки INSTRUCTION
/// от технических equip_info, созданных только для SCHEME.
/// </summary>
public sealed record InfoExportEquipmentInfoRow(
    string Equipment,
    string ProductCode,
    string Supplier,
    string Description,
    bool HasInstructionLinks,
    bool HasPhotoLinks);

/// <summary>
/// Один бинарный файл из equip_instruction или equip_photo.
/// </summary>
public sealed record InfoExportBinaryFile(
    long Id,
    string FileName,
    string FileHash,
    byte[] FileData);

/// <summary>
/// Один бинарный файл SCHEME вместе с областями назначения.
/// </summary>
public sealed record InfoExportSchemeFile(
    long Id,
    string FileName,
    string FileHash,
    byte[] FileData,
    string Station,
    string GroupNames,
    string Equipments);

/// <summary>
/// Область назначения строки листа SCHEME.
/// </summary>
public enum InfoExportSchemeScope
{
    Station,
    Group,
    Equipment
}

/// <summary>
/// Подготовленные данные листа SUPPLIER.
/// </summary>
public sealed record InfoExportWorkbookSupplierRow(
    string Supplier,
    string Logo);

/// <summary>
/// Подготовленные данные листа ORDERS.
/// </summary>
public sealed record InfoExportWorkbookOrderRow(
    string Type,
    string ProductCode,
    string Supplier,
    string Source,
    string Description,
    string Image);

/// <summary>
/// Подготовленные данные листа INSTRUCTION.
/// </summary>
public sealed record InfoExportWorkbookInstructionRow(
    string Station,
    string Type,
    string Equipment,
    string ProductCode,
    string Supplier,
    string Description);

/// <summary>
/// Одна строка одного из трёх блоков листа SCHEME.
/// </summary>
public sealed record InfoExportWorkbookSchemeRow(
    InfoExportSchemeScope Scope,
    string Target,
    string Source);

/// <summary>
/// Полный набор данных, который записывается в XLSX.
/// </summary>
public sealed record InfoExportWorkbookData(
    string InstructionRoot,
    string SchemeStationRoot,
    string SchemeGroupRoot,
    string SchemeEquipmentRoot,
    IReadOnlyList<InfoExportWorkbookSupplierRow> Suppliers,
    IReadOnlyList<InfoExportWorkbookOrderRow> Orders,
    IReadOnlyList<InfoExportWorkbookInstructionRow> Instructions,
    IReadOnlyList<InfoExportWorkbookSchemeRow> Schemes);

/// <summary>
/// Итог создания экспортного пакета.
/// </summary>
public sealed record InfoExportPackageResult(
    string ExcelFilePath,
    int SupplierCount,
    int OrderCount,
    int InstructionCount,
    int SchemeCount,
    int BinaryFileCount,
    bool RuntimeCatalogUsed,
    IReadOnlyList<string> Warnings);