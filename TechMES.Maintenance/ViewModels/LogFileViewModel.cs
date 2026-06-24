namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Log-файл, найденный в известных папках проекта.
/// Maintenance пока не навязывает формат логирования, а просто показывает последние строки файла.
/// </summary>
public sealed class LogFileViewModel
{
    /// <summary>
    /// Имя файла для списка.
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Полный путь к log-файлу.
    /// </summary>
    public string FullPath { get; init; } = "";

    /// <summary>
    /// Время последнего изменения файла.
    /// </summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>
    /// Размер файла в байтах.
    /// </summary>
    public long SizeBytes { get; init; }
}
