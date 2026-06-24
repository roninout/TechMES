using System.IO;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Один кандидат на сервисную очистку.
/// Строка хранит путь и тип объекта, а удаление выполняет CleanupService после явного клика оператора.
/// </summary>
public sealed class CleanupItemViewModel
{
    /// <summary>
    /// Логическая группа: Logs, Appsettings backup, Backup folder.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// Полный путь к файлу или папке.
    /// </summary>
    public string FullPath { get; init; } = "";

    /// <summary>
    /// True для папки, false для файла.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// Последнее время изменения объекта.
    /// </summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>
    /// Размер в байтах.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Размер в удобном виде.
    /// </summary>
    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : SizeBytes < 1024 * 1024
            ? $"{SizeBytes / 1024.0:0.0} KB"
            : $"{SizeBytes / 1024.0 / 1024.0:0.0} MB";

    /// <summary>
    /// Короткое имя для таблицы.
    /// </summary>
    public string Name => Path.GetFileName(FullPath);
}
