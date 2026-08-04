namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки Maintenance для ручного импорта, экспорта
/// и редактирования справочников Info-модуля.
///
/// Эти параметры относятся только к обслуживающему приложению
/// и не меняют поведение WEB/Runtime напрямую.
/// </summary>
public sealed class ImportEditOptions
{
    /// <summary>
    /// Последний выбранный Excel-файл комплексного импорта.
    /// </summary>
    public string ExcelImportFilePath { get; set; } = "";

    /// <summary>
    /// Полный путь к Excel-файлу экспортного пакета.
    ///
    /// Рядом с этим файлом автоматически создаются папки:
    /// Supplier_logo, Instruction и Scheme.
    /// </summary>
    public string ExcelExportFilePath { get; set; } = "";

    /// <summary>
    /// Папка-источник PDF-файлов ORDERS.
    /// </summary>
    public string OrdersPdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка-источник PDF-инструкций.
    /// </summary>
    public string InstructionPdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка-источник PDF-схем.
    /// </summary>
    public string SchemePdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка с логотипами поставщиков.
    /// </summary>
    public string SupplierLogoSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка с изображениями оборудования.
    /// </summary>
    public string InstructionImageSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка с растровыми изображениями схем.
    /// </summary>
    public string SchemeImageSourceRoot { get; set; } = "";
}