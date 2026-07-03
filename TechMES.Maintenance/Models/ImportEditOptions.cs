namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки Maintenance для ручного импорта и редактирования справочников Info-модуля.
/// Эти параметры относятся только к обслуживающему приложению и не меняют поведение WEB/Runtime напрямую.
/// </summary>
public sealed class ImportEditOptions
{
    /// <summary>
    /// Папка-источник, из которой оператор будет выбирать новые PDF-файлы для строк ORDERS.
    /// Сами записи ORDERS хранят в БД только имя/относительный путь файла, а этот путь помогает быстро брать файлы из общего архива.
    /// </summary>
    public string OrdersPdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Source folder for instruction PDF files imported into equip_instruction.
    /// </summary>
    public string InstructionPdfSourceRoot { get; set; } = "";

    /// <summary>
    /// Source folder for scheme PDF/image files imported into equip_scheme.
    /// </summary>
    public string SchemePdfSourceRoot { get; set; } = "";
}
