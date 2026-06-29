using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Создает и восстанавливает backup-снимки конфигурации TechMES.
/// Backup намеренно хранит только настройки и сертификаты: публикации, bin/obj и логи не копируются,
/// потому что их можно пересоздать через Deploy или они быстро становятся слишком большими.
/// </summary>
public sealed class BackupRestoreService(DirectoryInfo repositoryRoot)
{
    public const string ManifestFileName = "backup-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Возвращает список backup-снимков из выбранной папки.
    /// Поврежденные снимки не ломают весь список: они просто получают статус Cannot read manifest.
    /// </summary>
    public IReadOnlyList<BackupItemViewModel> FindBackups(string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(backupRoot) || !Directory.Exists(backupRoot))
            return [];

        return Directory
            .EnumerateDirectories(backupRoot)
            .Select(ReadBackupItem)
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Создает новый timestamp-снимок Maintenance, исходных appsettings, published appsettings и HTTPS-сертификатов.
    /// Отсутствующие файлы попадают в manifest как Exists=false, чтобы оператор видел полный ожидаемый состав.
    /// </summary>
    public BackupItemViewModel CreateBackup(
        MaintenanceConfiguration configuration,
        string backupRoot)
    {
        var createdAt = DateTime.Now;
        var folderName = createdAt.ToString("yyyyMMdd-HHmmss");
        var backupFolder = Path.Combine(backupRoot, folderName);

        Directory.CreateDirectory(backupFolder);

        var manifest = new BackupManifest
        {
            CreatedAt = createdAt,
            CreatedAtUtc = DateTime.UtcNow,
            RepositoryRoot = repositoryRoot.FullName,
            PublishRoot = configuration.Deployment.PublishRoot
        };

        AddBackupFile(
            backupFolder,
            manifest,
            "maintenance",
            Path.Combine(repositoryRoot.FullName, "TechMES.Maintenance", "maintenance.settings.json"),
            Path.Combine("source", "TechMES.Maintenance", "maintenance.settings.json"));

        foreach (var settingsFile in configuration.SettingsFiles)
        {
            AddBackupFile(
                backupFolder,
                manifest,
                "source-appsettings",
                Path.Combine(repositoryRoot.FullName, settingsFile.RelativePath),
                Path.Combine("source", settingsFile.RelativePath));
        }

        foreach (var service in configuration.Services.Where(service => !string.IsNullOrWhiteSpace(service.PublishFolderName) || !string.IsNullOrWhiteSpace(service.Key)))
        {
            var publishFolder = GetPublishFolderName(service);
            AddBackupFile(
                backupFolder,
                manifest,
                "published-appsettings",
                Path.Combine(configuration.Deployment.PublishRoot, publishFolder, "appsettings.json"),
                Path.Combine("published", publishFolder, "appsettings.json"));
        }

        AddBackupFile(
            backupFolder,
            manifest,
            "certificate",
            HttpsCertificateManager.GetPfxPath(configuration.Server),
            Path.Combine("certificates", configuration.Server.CertificateFileName));

        AddBackupFile(
            backupFolder,
            manifest,
            "certificate",
            HttpsCertificateManager.GetPublicCertificatePath(configuration.Server),
            Path.Combine("certificates", configuration.Server.PublicCertificateFileName));

        WriteManifest(backupFolder, manifest);
        return CreateItemViewModel(backupFolder, manifest);
    }

    /// <summary>
    /// Восстанавливает выбранный backup. Перед перезаписью существующего файла рядом создается .restore-backup копия,
    /// поэтому restore можно откатить вручную, если оператор выбрал не тот снимок.
    /// </summary>
    public string RestoreBackup(string backupFolder)
    {
        var manifest = ReadManifest(backupFolder);
        var restored = 0;
        var skipped = 0;
        var safetyStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        foreach (var file in manifest.Files.Where(file => file.Exists))
        {
            var backupPath = Path.Combine(backupFolder, file.BackupRelativePath);
            if (!File.Exists(backupPath))
            {
                skipped++;
                continue;
            }

            var targetFolder = Path.GetDirectoryName(file.SourcePath);
            if (!string.IsNullOrWhiteSpace(targetFolder))
                Directory.CreateDirectory(targetFolder);

            if (File.Exists(file.SourcePath))
                File.Copy(file.SourcePath, BuildSafetyCopyPath(file.SourcePath, safetyStamp), overwrite: false);

            File.Copy(backupPath, file.SourcePath, overwrite: true);
            restored++;
        }

        return skipped == 0
            ? $"Restored {restored} file(s). Restart services to apply published appsettings."
            : $"Restored {restored} file(s), skipped {skipped}. Restart services to apply published appsettings.";
    }

    /// <summary>
    /// Упаковывает выбранный backup-снимок в zip рядом с папкой backup root.
    /// Это удобно для переноса настроек на другой сервер или архивного хранения.
    /// </summary>
    public string ExportBackupZip(string backupFolder)
    {
        if (string.IsNullOrWhiteSpace(backupFolder) || !Directory.Exists(backupFolder))
            throw new DirectoryNotFoundException("Backup folder was not found.");

        var backupRoot = Path.GetDirectoryName(backupFolder)
            ?? throw new InvalidOperationException("Cannot resolve backup root.");
        var baseFileName = Path.GetFileName(backupFolder);
        var zipPath = BuildUniqueZipPath(backupRoot, baseFileName);

        ZipFile.CreateFromDirectory(backupFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    /// <summary>
    /// Импортирует zip-архив backup-снимка в выбранный backup root.
    /// Если папка с таким именем уже есть, добавляет числовой суффикс.
    /// </summary>
    public BackupItemViewModel ImportBackupZip(
        string zipPath,
        string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new FileNotFoundException("Backup zip file was not found.", zipPath);

        if (string.IsNullOrWhiteSpace(backupRoot))
            throw new InvalidOperationException("Backup root is empty.");

        Directory.CreateDirectory(backupRoot);

        var folderName = Path.GetFileNameWithoutExtension(zipPath);
        var destination = BuildUniqueFolderPath(backupRoot, folderName);
        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination);

        if (!File.Exists(Path.Combine(destination, ManifestFileName)))
            throw new InvalidOperationException("Imported zip does not contain a TechMES backup manifest.");

        return ReadBackupItem(destination);
    }

    /// <summary>
    /// Добавляет файл в backup и manifest. Если исходного файла нет, manifest все равно сохраняет ожидаемый путь.
    /// </summary>
    private static void AddBackupFile(
        string backupFolder,
        BackupManifest manifest,
        string kind,
        string sourcePath,
        string backupRelativePath)
    {
        var item = new BackupManifestFile
        {
            Kind = kind,
            SourcePath = sourcePath,
            BackupRelativePath = backupRelativePath,
            Exists = File.Exists(sourcePath)
        };

        if (item.Exists)
        {
            var destinationPath = Path.Combine(backupFolder, backupRelativePath);
            var destinationFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            File.Copy(sourcePath, destinationPath, overwrite: true);
            item.SizeBytes = new FileInfo(destinationPath).Length;
        }

        manifest.Files.Add(item);
    }

    /// <summary>
    /// Читает manifest и превращает его в строку таблицы backup-снимков.
    /// </summary>
    private static BackupItemViewModel ReadBackupItem(string backupFolder)
    {
        try
        {
            return CreateItemViewModel(backupFolder, ReadManifest(backupFolder));
        }
        catch
        {
            return new BackupItemViewModel
            {
                DisplayName = Path.GetFileName(backupFolder),
                FullPath = backupFolder,
                CreatedAt = Directory.GetCreationTime(backupFolder),
                Summary = "Cannot read manifest."
            };
        }
    }

    /// <summary>
    /// Сохраняет manifest в JSON рядом с файлами backup.
    /// </summary>
    private static void WriteManifest(
        string backupFolder,
        BackupManifest manifest)
    {
        File.WriteAllText(
            Path.Combine(backupFolder, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>
    /// Загружает manifest выбранного backup.
    /// </summary>
    private static BackupManifest ReadManifest(string backupFolder)
    {
        var manifestPath = Path.Combine(backupFolder, ManifestFileName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), JsonOptions);
        return manifest ?? throw new InvalidOperationException("Backup manifest is empty.");
    }

    /// <summary>
    /// Формирует строку таблицы из manifest.
    /// </summary>
    private static BackupItemViewModel CreateItemViewModel(
        string backupFolder,
        BackupManifest manifest)
    {
        var existingFiles = manifest.Files.Where(file => file.Exists).ToList();
        var kinds = existingFiles
            .GroupBy(file => file.Kind)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToArray();

        return new BackupItemViewModel
        {
            DisplayName = Path.GetFileName(backupFolder),
            FullPath = backupFolder,
            CreatedAt = manifest.CreatedAt == default ? Directory.GetCreationTime(backupFolder) : manifest.CreatedAt,
            FileCount = existingFiles.Count,
            SizeBytes = existingFiles.Sum(file => file.SizeBytes),
            Summary = kinds.Length == 0 ? "No files were copied." : string.Join(", ", kinds)
        };
    }

    /// <summary>
    /// Повторяет правило DeploymentManager: если PublishFolderName пустой, берем Key, затем ServiceName.
    /// </summary>
    private static string GetPublishFolderName(ServiceDefinition service)
    {
        if (!string.IsNullOrWhiteSpace(service.PublishFolderName))
            return service.PublishFolderName;

        return !string.IsNullOrWhiteSpace(service.Key) ? service.Key : service.ServiceName;
    }

    /// <summary>
    /// Строит уникальное имя защитной копии для файла, который будет перезаписан restore.
    /// </summary>
    private static string BuildSafetyCopyPath(
        string sourcePath,
        string safetyStamp)
    {
        var safetyPath = $"{sourcePath}.restore-backup-{safetyStamp}";
        if (!File.Exists(safetyPath))
            return safetyPath;

        for (var index = 2; ; index++)
        {
            var candidate = $"{safetyPath}-{index}";
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Возвращает свободное имя zip-файла, чтобы экспорт не перезатер старый архив.
    /// </summary>
    private static string BuildUniqueZipPath(
        string folder,
        string baseFileName)
    {
        var candidate = Path.Combine(folder, $"{baseFileName}.zip");
        if (!File.Exists(candidate))
            return candidate;

        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(folder, $"{baseFileName}-{index}.zip");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Возвращает свободное имя папки для импортируемого backup.
    /// </summary>
    private static string BuildUniqueFolderPath(
        string folder,
        string baseFolderName)
    {
        var safeName = string.IsNullOrWhiteSpace(baseFolderName)
            ? DateTime.Now.ToString("yyyyMMdd-HHmmss")
            : baseFolderName;
        var candidate = Path.Combine(folder, safeName);
        if (!Directory.Exists(candidate))
            return candidate;

        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(folder, $"{safeName}-{index}");
            if (!Directory.Exists(candidate))
                return candidate;
        }
    }
}
