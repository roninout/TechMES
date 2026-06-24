namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка списка backup-снимков во вкладке Backup / Restore.
/// UI не читает manifest напрямую: сервис собирает короткое и безопасное представление для таблицы.
/// </summary>
public sealed class BackupItemViewModel
{
    /// <summary>
    /// Имя папки backup, обычно timestamp вида 20260624-153000.
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Полный путь к папке backup.
    /// </summary>
    public string FullPath { get; init; } = "";

    /// <summary>
    /// Время создания backup.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Сколько реальных файлов сохранено в снимке.
    /// </summary>
    public int FileCount { get; init; }

    /// <summary>
    /// Общий размер сохраненных файлов.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Короткая расшифровка содержимого backup.
    /// </summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Размер в удобном для чтения виде.
    /// </summary>
    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:0.0} KB";
}
