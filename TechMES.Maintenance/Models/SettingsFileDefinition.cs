namespace TechMES.Maintenance.Models;

/// <summary>
/// Описание файла настроек, который можно редактировать из Maintenance.
/// На первом этапе работаем с обычным JSON-текстом и обязательной проверкой валидности перед сохранением.
/// </summary>
public sealed class SettingsFileDefinition
{
    /// <summary>
    /// Название файла в списке настроек.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Путь к файлу относительно корня репозитория TechMES.
    /// </summary>
    public string RelativePath { get; set; } = "";
}
