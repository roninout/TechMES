namespace TechMES.Maintenance.Models;

/// <summary>
/// Полностью прочитанный Excel-документ импорта Info-модуля.
/// Модель не содержит логики БД: она описывает только исходные строки и папки из книги.
/// </summary>
public sealed class ExcelInfoImportDocument
{
    /// <summary>
    /// Корневая папка инструкций из ячейки INSTRUCTION!B1.
    /// </summary>
    public string InstructionRoot { get; init; } = "";

    /// <summary>
    /// Корневая папка станционных схем из SCHEME!A1.
    /// </summary>
    public string SchemeStationRoot { get; init; } = "";

    /// <summary>
    /// Корневая папка групповых схем из SCHEME!D1.
    /// </summary>
    public string SchemeGroupRoot { get; init; } = "";

    /// <summary>
    /// Корневая папка схем отдельных единиц оборудования из SCHEME!G1.
    /// </summary>
    public string SchemeEquipmentRoot { get; init; } = "";

    public IReadOnlyList<ExcelSupplierImportRow> Suppliers { get; init; } = [];
    public IReadOnlyList<ExcelOrderImportRow> Orders { get; init; } = [];
    public IReadOnlyList<ExcelInstructionImportRow> Instructions { get; init; } = [];
    public IReadOnlyList<ExcelSchemeImportRow> Schemes { get; init; } = [];
}

/// <summary>
/// Строка листа SUPPLIER.
/// </summary>
public sealed record ExcelSupplierImportRow(string Supplier, string LogoFileName);

/// <summary>
/// Строка листа ORDERS. Source и Image могут содержать несколько файлов через запятую.
/// </summary>
public sealed record ExcelOrderImportRow(
    string Type,
    string ProductCode,
    string Supplier,
    string Source,
    string Description,
    string Image);

/// <summary>
/// Строка листа INSTRUCTION. ProductCode связывает оборудование с записью ORDERS.
/// </summary>
public sealed record ExcelInstructionImportRow(
    string Station,
    string Type,
    string Equipment,
    string ProductCode,
    string Supplier,
    string Description);

/// <summary>
/// Область применения записи листа SCHEME.
/// </summary>
public enum ExcelSchemeScope
{
    Station,
    Group,
    Equipment
}

/// <summary>
/// Одна связь схемы со станцией, группой или отдельным оборудованием.
/// </summary>
public sealed record ExcelSchemeImportRow(
    ExcelSchemeScope Scope,
    string Target,
    string SourceRoot,
    string Source);
