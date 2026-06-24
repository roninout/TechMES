using System.IO;
using System.Text;
using System.Text.Json;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Читает и сохраняет appsettings-файлы WEB/Runtime.
/// Перед записью проверяет JSON и создает backup рядом с исходным файлом.
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
    /// Проверяет JSON, создает backup и сохраняет новый текст.
    /// Возвращает путь к backup-файлу для отображения пользователю.
    /// </summary>
    public string Save(string path, string content)
    {
        using (JsonDocument.Parse(content))
        {
            // Parse сам выбросит JsonException, если текст нельзя безопасно сохранить как appsettings.
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var backupPath = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";

        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: false);

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return backupPath;
    }
}
