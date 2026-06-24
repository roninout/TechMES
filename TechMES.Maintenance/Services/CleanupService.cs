using System.IO;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Собирает кандидатов на очистку и удаляет только явно найденные производные файлы.
/// Сервис не трогает рабочие appsettings, публикации dll/exe и исходный код.
/// </summary>
public sealed class CleanupService(DirectoryInfo repositoryRoot)
{
    /// <summary>
    /// Ищет старые log-файлы, appsettings-backup файлы и старые backup-папки.
    /// </summary>
    public IReadOnlyList<CleanupItemViewModel> Scan(
        MaintenanceConfiguration configuration,
        string backupRoot)
    {
        var items = new List<CleanupItemViewModel>();
        var now = DateTime.Now;

        AddOldLogFiles(items, configuration, now.AddDays(-configuration.Cleanup.LogRetentionDays));
        AddOldAppsettingsBackups(items, configuration, now.AddDays(-configuration.Cleanup.AppSettingsBackupRetentionDays));
        AddOldBackupFolders(items, backupRoot, now.AddDays(-configuration.Cleanup.BackupRetentionDays));

        return items
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.LastWriteTime)
            .ToList();
    }

    /// <summary>
    /// Удаляет переданные кандидаты и возвращает человекочитаемый результат.
    /// </summary>
    public string Delete(IReadOnlyList<CleanupItemViewModel> items)
    {
        var deleted = 0;
        var failed = 0;
        long releasedBytes = 0;

        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory)
                {
                    if (Directory.Exists(item.FullPath))
                        Directory.Delete(item.FullPath, recursive: true);
                }
                else if (File.Exists(item.FullPath))
                {
                    File.Delete(item.FullPath);
                }

                deleted++;
                releasedBytes += item.SizeBytes;
            }
            catch
            {
                failed++;
            }
        }

        return failed == 0
            ? $"Deleted {deleted} item(s), released {FormatBytes(releasedBytes)}."
            : $"Deleted {deleted} item(s), failed {failed}, released {FormatBytes(releasedBytes)}.";
    }

    /// <summary>
    /// Возвращает состояние дисков для publish root, backup root и репозитория.
    /// </summary>
    public IReadOnlyList<DiskStatusViewModel> GetDiskStatuses(
        MaintenanceConfiguration configuration,
        string backupRoot)
    {
        return
        [
            CreateDiskStatus("Repository", repositoryRoot.FullName),
            CreateDiskStatus("Publish root", configuration.Deployment.PublishRoot),
            CreateDiskStatus("Backup root", backupRoot)
        ];
    }

    /// <summary>
    /// Добавляет log-файлы старше retention.
    /// </summary>
    private static void AddOldLogFiles(
        List<CleanupItemViewModel> items,
        MaintenanceConfiguration configuration,
        DateTime threshold)
    {
        var logRoots = new[]
        {
            Path.Combine(configuration.Deployment.PublishRoot, "Runtime.Service", "logs"),
            Path.Combine(configuration.Deployment.PublishRoot, "Web", "logs")
        };

        foreach (var root in logRoots)
            AddFiles(items, "Logs", root, "*.log", threshold);
    }

    /// <summary>
    /// Добавляет side-backup файлы appsettings: *.bak и *.restore-backup-*.
    /// </summary>
    private void AddOldAppsettingsBackups(
        List<CleanupItemViewModel> items,
        MaintenanceConfiguration configuration,
        DateTime threshold)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            repositoryRoot.FullName,
            configuration.Deployment.PublishRoot
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            AddFiles(items, "Appsettings backup", root, "*.bak", threshold);
            AddFiles(items, "Appsettings backup", root, "*.restore-backup-*", threshold);
        }
    }

    /// <summary>
    /// Добавляет старые timestamp-папки Backup / Restore.
    /// </summary>
    private static void AddOldBackupFolders(
        List<CleanupItemViewModel> items,
        string backupRoot,
        DateTime threshold)
    {
        if (!Directory.Exists(backupRoot))
            return;

        foreach (var folder in Directory.EnumerateDirectories(backupRoot))
        {
            var info = new DirectoryInfo(folder);
            if (info.LastWriteTime >= threshold)
                continue;

            items.Add(new CleanupItemViewModel
            {
                Kind = "Backup folder",
                FullPath = info.FullName,
                IsDirectory = true,
                LastWriteTime = info.LastWriteTime,
                SizeBytes = GetDirectorySize(info)
            });
        }
    }

    /// <summary>
    /// Добавляет файлы по маске, если они старше threshold.
    /// </summary>
    private static void AddFiles(
        List<CleanupItemViewModel> items,
        string kind,
        string root,
        string pattern,
        DateTime threshold)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.LastWriteTime >= threshold)
                continue;

            items.Add(new CleanupItemViewModel
            {
                Kind = kind,
                FullPath = info.FullName,
                IsDirectory = false,
                LastWriteTime = info.LastWriteTime,
                SizeBytes = info.Length
            });
        }
    }

    /// <summary>
    /// Считает размер папки рекурсивно.
    /// </summary>
    private static long GetDirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Строит строку состояния диска.
    /// </summary>
    private static DiskStatusViewModel CreateDiskStatus(
        string name,
        string path)
    {
        try
        {
            var root = Directory.Exists(path)
                ? Path.GetPathRoot(Path.GetFullPath(path))
                : Path.GetPathRoot(Path.GetFullPath(Path.GetDirectoryName(path) ?? path));

            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Cannot resolve drive root.");

            var drive = new DriveInfo(root);
            var freePercent = drive.TotalSize == 0 ? 0 : drive.AvailableFreeSpace * 100.0 / drive.TotalSize;

            return new DiskStatusViewModel
            {
                Name = name,
                Path = path,
                Drive = drive.Name,
                FreeText = FormatBytes(drive.AvailableFreeSpace),
                TotalText = FormatBytes(drive.TotalSize),
                Status = freePercent < 10 ? "Warning" : "OK"
            };
        }
        catch (Exception ex)
        {
            return new DiskStatusViewModel
            {
                Name = name,
                Path = path,
                Drive = "-",
                FreeText = "-",
                TotalText = "-",
                Status = ex.Message
            };
        }
    }

    /// <summary>
    /// Форматирует байты в короткий вид для таблиц Maintenance.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024L * 1024L)
            return $"{bytes / 1024.0:0.0} KB";

        if (bytes < 1024L * 1024L * 1024L)
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";

        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB";
    }
}
