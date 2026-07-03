using System.Net.Http;
using System.Text.Json;
using TechMES.Contracts.Equipment;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Мини-клиент Runtime.Service для Maintenance.
/// Он нужен не для постоянного опроса, а только для Import/Edit вкладок, где оператор должен привязать документы к станциям, типам и оборудованию.
/// </summary>
public sealed class RuntimeCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Загружает каталог оборудования из Runtime /api/equipment и сжимает его до списков, удобных для UI импорта.
    /// </summary>
    public async Task<RuntimeCatalogSnapshot> LoadEquipmentCatalogAsync(
        string runtimeBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(runtimeBaseUrl);

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        await using var stream = await http.GetStreamAsync(endpoint, cancellationToken);
        var response = await JsonSerializer.DeserializeAsync<EquipmentListResponse>(
            stream,
            JsonOptions,
            cancellationToken);

        if (response is null)
            throw new InvalidOperationException("Runtime returned an empty equipment response.");

        var equipments = response.Equipments
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        //var types = response.Equipments
        //    .Select(x => string.IsNullOrWhiteSpace(x.TypeName) ? x.TypeGroup.ToString() : x.TypeName)
        //    .Where(x => !string.IsNullOrWhiteSpace(x))
        //    .Distinct(StringComparer.OrdinalIgnoreCase)
        //    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        //    .ToList();

        /*
         * TypeName содержит исходный SCADA Equipment Type:
         * AnalogIn, DigitalIn, ValveA и т. д.
         *
         * Для ORDERS нужен пользовательский алиас, уже рассчитанный Runtime
         * в TypeGroup: AI, DI, VGA, ATV и т. д.
         */
        var types = response.Equipments
            .Select(x => x.TypeGroup.ToString())
            .Where(IsVisibleRuntimeTypeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RuntimeCatalogSnapshot
        {
            Stations = response.Stations
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Types = types,
            Equipments = equipments,
            TotalCount = response.TotalCount
        };
    }

    /// <summary>
    /// Исключает служебные группы, которые не должны попадать в ORDERS Type.
    /// </summary>
    private static bool IsVisibleRuntimeTypeAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("All", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("Favorites", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Собирает абсолютный URL Runtime /api/equipment из base URL, который WEB обычно хранит в appsettings как RuntimeService:BaseUrl.
    /// </summary>
    private static Uri BuildEndpoint(string runtimeBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(runtimeBaseUrl))
            throw new InvalidOperationException("Runtime base URL is empty.");

        var normalized = runtimeBaseUrl.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";

        return new Uri(new Uri(normalized, UriKind.Absolute), "api/equipment");
    }
}
