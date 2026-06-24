using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Загружает настройки обслуживающего приложения.
/// Файл хранится рядом с проектом Maintenance, чтобы его можно было редактировать как часть solution.
/// </summary>
public sealed class MaintenanceConfigurationStore(DirectoryInfo repositoryRoot)
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,

        // maintenance.settings.json часто редактируется вручную.
        // Разрешаем JSONC-комментарии и trailing comma, чтобы временно отключать строки через // без сброса на дефолт.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Полный путь к maintenance.settings.json в исходниках.
    /// </summary>
    public string ConfigurationPath =>
        Path.Combine(repositoryRoot.FullName, "TechMES.Maintenance", "maintenance.settings.json");

    /// <summary>
    /// Читает конфигурацию Maintenance.
    /// Если файл отсутствует или JSON поврежден, возвращает дефолтную конфигурацию и не ломает запуск UI.
    /// </summary>
    public MaintenanceConfiguration Load()
    {
        if (!File.Exists(ConfigurationPath))
            return MaintenanceConfiguration.CreateDefault();

        try
        {
            var json = File.ReadAllText(ConfigurationPath);
            return JsonSerializer.Deserialize<MaintenanceConfiguration>(json, _jsonOptions)
                ?? MaintenanceConfiguration.CreateDefault();
        }
        catch
        {
            return MaintenanceConfiguration.CreateDefault();
        }
    }

    /// <summary>
    /// Сохраняет конфигурацию Maintenance.
    /// На первом этапе UI ее только читает, но метод оставлен для будущей страницы редактирования.
    /// </summary>
    public void Save(MaintenanceConfiguration configuration)
    {
        var json = JsonSerializer.Serialize(configuration, _jsonOptions);
        File.WriteAllText(ConfigurationPath, json);
    }
}
