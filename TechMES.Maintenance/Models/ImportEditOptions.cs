namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки Maintenance для ручного импорта и редактирования справочников Info-модуля.
/// Эти параметры относятся только к обслуживающему приложению и не меняют поведение WEB/Runtime напрямую.
/// </summary>
public sealed class ImportEditOptions
{
    /// <summary>
    /// Последний выбранный Excel-файл комплексного импорта.
    /// Путь сохраняется, чтобы оператор мог повторить импорт после проверки или исправления файла.
    /// </summary>
    public string ExcelImportFilePath { get; set; } = "";

    /// <summary>
    /// Папка-источник, из которой оператор будет выбирать новые PDF-файлы для строк ORDERS.
    /// Сами записи ORDERS хранят в БД только имя/относительный путь файла, а этот путь помогает быстро брать файлы из общего архива.
    /// </summary>
    public string OrdersPdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка-источник PDF-инструкций, сохраняемых в equip_info_instruction.
    /// </summary>
    public string InstructionPdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка-источник PDF-схем, сохраняемых в equip_info_scheme.
    /// </summary>
    public string SchemePdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка с логотипами поставщиков для листа SUPPLIER.
    /// </summary>
    public string SupplierLogoSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка с изображениями оборудования, указанными в строках ORDERS/INSTRUCTION.
    /// </summary>
    public string InstructionImageSourceRoot { get; set; } = "";

    /// <summary>
    /// Папка с растровыми изображениями схем.
    /// PDF и изображения разделены, чтобы одинаковые имена файлов не создавали неоднозначность.
    /// </summary>
    public string SchemeImageSourceRoot { get; set; } = "";
}
