namespace TechMES.Maintenance.Models;

/// <summary>
/// Описание одного backup-снимка Maintenance.
/// Файл manifest лежит рядом с копиями файлов и нужен, чтобы restore точно знал,
/// откуда был взят каждый файл и куда его нужно вернуть.
/// </summary>
public sealed class BackupManifest
{
    /// <summary>
    /// Версия структуры manifest. При изменении формата restore сможет отличить старые backup-снимки.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Локальное время создания backup. Его удобно показывать оператору в UI.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC-время создания backup. Оно не зависит от часового пояса сервера.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Корень репозитория, из которого был создан backup.
    /// </summary>
    public string RepositoryRoot { get; set; } = "";

    /// <summary>
    /// Корень опубликованной серверной установки, например C:\TechMES.
    /// </summary>
    public string PublishRoot { get; set; } = "";

    /// <summary>
    /// Список файлов, которые Maintenance пытался сохранить в backup.
    /// </summary>
    public List<BackupManifestFile> Files { get; set; } = [];
}

/// <summary>
/// Одна строка manifest: исходный путь, путь внутри backup и служебная классификация файла.
/// </summary>
public sealed class BackupManifestFile
{
    /// <summary>
    /// Тип файла: maintenance, source-appsettings, published-appsettings, certificate.
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// Путь к файлу в рабочей системе, куда restore вернет сохраненную копию.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// Относительный путь файла внутри папки backup.
    /// </summary>
    public string BackupRelativePath { get; set; } = "";

    /// <summary>
    /// Был ли файл найден во время создания backup.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Размер сохраненной копии в байтах. Для отсутствующих файлов остается 0.
    /// </summary>
    public long SizeBytes { get; set; }
}
