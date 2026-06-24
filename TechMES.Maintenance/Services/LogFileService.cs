using System.IO;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Ищет и читает log-файлы проекта.
/// На первом этапе это простой просмотрщик последних строк, без привязки к конкретному logging provider.
/// </summary>
public sealed class LogFileService(DirectoryInfo repositoryRoot)
{
    /// <summary>
    /// Ищет log/txt-файлы в заданных папках относительно корня репозитория.
    /// Ограничиваем список, чтобы случайно не просканировать слишком большой каталог.
    /// </summary>
    public IReadOnlyList<LogFileViewModel> FindLogs(
        IEnumerable<string> relativeRoots,
        string? publishRoot)
    {
        var files = new List<FileInfo>();

        foreach (var fullRoot in BuildSearchRoots(relativeRoots, publishRoot))
        {
            if (!Directory.Exists(fullRoot))
                continue;

            files.AddRange(EnumerateFilesSafe(fullRoot, "*.log"));
            files.AddRange(EnumerateFilesSafe(fullRoot, "*.txt"));
        }

        return files
            .DistinctBy(file => file.FullName)
            .OrderByDescending(file => file.LastWriteTime)
            .Take(100)
            .Select(file => new LogFileViewModel
            {
                DisplayName = GetDisplayName(file.FullName),
                FullPath = file.FullName,
                LastWriteTime = file.LastWriteTime,
                SizeBytes = file.Length
            })
            .ToList();
    }

    /// <summary>
    /// Читает последние строки файла.
    /// Этого достаточно для диагностики запуска и не требует загружать в память весь большой лог.
    /// </summary>
    public string ReadTail(string path, int maxLines = 400)
    {
        if (!File.Exists(path))
            return "Log file not found.";

        var lines = File.ReadLines(path).TakeLast(maxLines);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Безопасно перечисляет файлы и проглатывает ошибки доступа к отдельным каталогам.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateFilesSafe(string root, string pattern)
    {
        try
        {
            return new DirectoryInfo(root)
                .EnumerateFiles(pattern, SearchOption.AllDirectories)
                .Where(file => !file.FullName.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                    && !file.FullName.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Собирает реальные папки поиска.
    /// Кроме исходников добавляем Deployment:PublishRoot, потому что Windows Services пишут файлы
    /// уже в опубликованные папки C:\TechMES\Runtime.Service\logs и C:\TechMES\Web\logs.
    /// </summary>
    private IEnumerable<string> BuildSearchRoots(
        IEnumerable<string> relativeRoots,
        string? publishRoot)
    {
        foreach (var root in relativeRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            yield return Path.IsPathRooted(root)
                ? root
                : Path.Combine(repositoryRoot.FullName, root);
        }

        if (!string.IsNullOrWhiteSpace(publishRoot))
            yield return publishRoot;
    }

    /// <summary>
    /// Показывает путь относительно репозитория, если файл лежит в исходниках,
    /// иначе оставляет полный путь к опубликованному серверному логу.
    /// </summary>
    private string GetDisplayName(string fullPath)
    {
        if (fullPath.StartsWith(repositoryRoot.FullName, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(repositoryRoot.FullName, fullPath);

        return fullPath;
    }
}
