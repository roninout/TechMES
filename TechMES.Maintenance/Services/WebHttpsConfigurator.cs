using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Прописывает HTTPS endpoint Kestrel в рабочий appsettings TechMES.Web.
/// Если WEB уже опубликован, меняем published appsettings; иначе меняем исходный appsettings проекта.
/// </summary>
public sealed class WebHttpsConfigurator(DirectoryInfo repositoryRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Обновляет все найденные WEB appsettings, чтобы Kestrel слушал HTTP и HTTPS одновременно.
    /// HTTP оставляем рабочим fallback-каналом; redirect на HTTPS управляется отдельным флагом.
    /// </summary>
    public ServiceCommandResult ApplyHttpsConfiguration(MaintenanceConfiguration configuration)
    {
        var paths = GetCandidateAppsettingsPaths(configuration);
        var updatedPaths = new List<string>();
        var missingPaths = new List<string>();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                missingPaths.Add(path);
                continue;
            }

            UpdateWebAppsettings(path, configuration.Server);
            updatedPaths.Add(path);
        }

        return new ServiceCommandResult
        {
            Success = updatedPaths.Count > 0,
            Status = updatedPaths.Count > 0 ? "HTTPS config applied" : "WEB appsettings not found",
            Details = BuildDetails(updatedPaths, missingPaths)
        };
    }

    /// <summary>
    /// Возвращает один рабочий WEB appsettings.
    /// Опубликованный путь вычисляем из того же Deployment-профиля, которым пользуется вкладка Deploy.
    /// </summary>
    private IReadOnlyList<string> GetCandidateAppsettingsPaths(MaintenanceConfiguration configuration)
    {
        var sourcePath = Path.Combine(repositoryRoot.FullName, "TechMES.Web", "appsettings.json");
        var webService = configuration.Services.FirstOrDefault(service =>
            string.Equals(service.Key, "web", StringComparison.OrdinalIgnoreCase)
            || string.Equals(service.ServiceName, "TechMES.Web", StringComparison.OrdinalIgnoreCase));

        var publishFolderName = webService?.PublishFolderName;

        if (string.IsNullOrWhiteSpace(publishFolderName))
            publishFolderName = "Web";

        var publishedPath = Path.Combine(
            configuration.Deployment.PublishRoot,
            publishFolderName,
            "appsettings.json");

        return File.Exists(publishedPath)
            ? [publishedPath]
            : [sourcePath];
    }

    /// <summary>
    /// Вносит Kestrel:Endpoints:Http/Https и HttpsRedirection:Enabled в один JSON-файл.
    /// </summary>
    private static void UpdateWebAppsettings(
        string appsettingsPath,
        ServerOptions server)
    {
        var root = ReadJsonObject(appsettingsPath);
        var pfxPath = HttpsCertificateManager.GetPfxPath(server);

        root["Urls"] = $"http://0.0.0.0:{server.WebPort};https://0.0.0.0:{server.HttpsPort}";

        var kestrel = GetOrCreateObject(root, "Kestrel");
        var endpoints = GetOrCreateObject(kestrel, "Endpoints");

        var http = GetOrCreateObject(endpoints, "Http");
        http["Url"] = $"http://0.0.0.0:{server.WebPort}";

        var https = GetOrCreateObject(endpoints, "Https");
        https["Url"] = $"https://0.0.0.0:{server.HttpsPort}";

        var certificate = GetOrCreateObject(https, "Certificate");
        certificate["Path"] = pfxPath;
        certificate["PublicPath"] = HttpsCertificateManager.GetPublicCertificatePath(server);
        certificate["Password"] = server.CertificatePassword;

        var httpsRedirection = GetOrCreateObject(root, "HttpsRedirection");
        httpsRedirection["Enabled"] = server.EnableHttpsRedirection;

        File.WriteAllText(
            appsettingsPath,
            root.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// Читает JSON как объект. Если файл пустой или поврежден, бросаем понятную ошибку вместо молчаливой порчи.
    /// </summary>
    private static JsonObject ReadJsonObject(string path)
    {
        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json);

        return node as JsonObject
            ?? throw new InvalidOperationException($"Root JSON node is not an object: {path}");
    }

    /// <summary>
    /// Возвращает дочерний JSON-объект или создает новый, если раздела еще нет.
    /// </summary>
    private static JsonObject GetOrCreateObject(
        JsonObject parent,
        string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    /// <summary>
    /// Формирует человекочитаемый лог применения HTTPS-настроек.
    /// </summary>
    private static string BuildDetails(
        IReadOnlyList<string> updatedPaths,
        IReadOnlyList<string> missingPaths)
    {
        var lines = new List<string>();

        if (updatedPaths.Count > 0)
        {
            lines.Add("Updated:");
            lines.AddRange(updatedPaths.Select(path => "  " + path));
        }

        if (missingPaths.Count > 0)
        {
            lines.Add("Skipped missing:");
            lines.AddRange(missingPaths.Select(path => "  " + path));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
