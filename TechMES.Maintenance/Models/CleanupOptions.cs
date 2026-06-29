namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки сервисной очистки.
/// Maintenance удаляет только производные файлы: логи, локальные .bak/.restore-backup копии и старые backup-снимки.
/// Рабочие appsettings, опубликованные exe/dll и исходники не попадают в кандидаты на удаление.
/// </summary>
public sealed class CleanupOptions
{
    /// <summary>
    /// Папка, в которой Maintenance хранит backup-снимки и zip-экспорты.
    /// Если поле пустое, приложение использует TechMES.Maintenance\backups внутри репозитория.
    /// </summary>
    public string BackupRoot { get; set; } = "";

    /// <summary>
    /// Сколько дней хранить log-файлы Runtime/Web.
    /// </summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>
    /// Сколько дней хранить side-backup файлы рядом с appsettings: *.bak и *.restore-backup-*.
    /// </summary>
    public int AppSettingsBackupRetentionDays { get; set; } = 30;

    /// <summary>
    /// Сколько дней хранить папки Backup / Restore.
    /// </summary>
    public int BackupRetentionDays { get; set; } = 60;
}
