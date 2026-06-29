using System.IO;
using System.Text;
using System.Text.Json;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Читает и сохраняет appsettings-файлы WEB/Runtime.
/// Основной путь сохранения проверяет JSON и пишет файл напрямую; явные снимки делает вкладка Backup / Restore.
/// </summary>
public sealed class SettingsFileService
{
    /// <summary>
    /// Читает текст файла настроек.
    /// </summary>
    public string Load(string path)
    {
        return File.Exists(path)
            ? File.ReadAllText(path)
            : "";
    }

    /// <summary>
    /// Проверяет JSON и сохраняет файл напрямую, без создания соседнего .bak-файла.
    /// Явные снимки конфигурации теперь делаются вкладкой Backup / Restore.
    /// </summary>
    public void SaveWithoutBackup(string path, string content)
    {
        using (JsonDocument.Parse(content))
        {
            // Parse сам выбросит JsonException, если текст нельзя безопасно сохранить как appsettings.
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
