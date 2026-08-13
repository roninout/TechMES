using System.Net.Http.Json;
using System.Text.Json;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Scada;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// HTTP-реализация клиента Runtime.Service.
/// </summary>
public sealed class RuntimeCalcClient(HttpClient httpClient) : IRuntimeCalcClient
{
    private const int MaximumBatchSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Просит Runtime выполнить явный CtApi scan Calc models.
    ///
    /// Этот метод вызывается только после получения execution lease.
    /// Standby Calc.Service scan не запускает.
    /// </summary>
    public async Task<CalcModelCatalogResponse> RefreshModelCatalogAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/calc/models/refresh");
        using var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(ct);

            var message = $"Runtime Calc model catalog refresh failed with HTTP {(int)response.StatusCode}.";

            if (!string.IsNullOrWhiteSpace(responseText))
                message += $" Response: {LimitText(responseText, 500)}";

            throw new HttpRequestException(message, null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<CalcModelCatalogResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty Calc model catalog response.");
    }

    /// <summary>
    /// Загружает текущий read-only snapshot enabled-заданий.
    /// </summary>
    public async Task<CalcConfigurationSnapshotDto> GetConfigurationSnapshotAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync("api/calc/configuration/snapshot", ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(ct);
            var message = $"Runtime configuration snapshot request failed with HTTP {(int)response.StatusCode}.";

            if (!string.IsNullOrWhiteSpace(responseText))
                message += $" Response: {LimitText(responseText, 500)}";

            throw new HttpRequestException(message, null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<CalcConfigurationSnapshotDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty calculation configuration snapshot.");
    }

    /// <summary>
    /// Читает уникальные теги через один или несколько ограниченных batch.
    ///
    /// При количестве до 500 тегов выполняется ровно один HTTP-запрос.
    /// </summary>
    public async Task<ScadaTagBatchReadResponse> ReadTagsAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagNames);

        var normalizedNames = tagNames
            .Select(tagName => (tagName ?? "").Trim())
            .Where(tagName => tagName.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedNames.Count == 0)
        {
            return new ScadaTagBatchReadResponse
            {
                ReadAtUtc = DateTimeOffset.UtcNow,
                RequestedCount = tagNames.Count
            };
        }

        var items = new List<ScadaTagBatchReadItem>(normalizedNames.Count);
        var latestReadAtUtc = DateTimeOffset.MinValue;

        foreach (var chunk in normalizedNames.Chunk(MaximumBatchSize))
        {
            var response = await ReadTagChunkAsync(chunk, ct);
            items.AddRange(response.Items);

            if (response.ReadAtUtc > latestReadAtUtc)
                latestReadAtUtc = response.ReadAtUtc;
        }

        return new ScadaTagBatchReadResponse
        {
            ReadAtUtc = latestReadAtUtc,
            RequestedCount = tagNames.Count,
            UniqueCount = normalizedNames.Count,
            SuccessCount = items.Count(item => item.Success),
            FailureCount = items.Count(item => !item.Success),
            Items = items
        };
    }

    /// <summary>
    /// Сохраняет пакет диагностических результатов через Runtime.Service.
    /// </summary>
    public async Task<CalcExecutionResultBatchResponse> SaveExecutionResultsAsync(CalcExecutionResultBatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await httpClient.PostAsJsonAsync(
            "api/calc/execution/results",
            request,
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(ct);
            var message = $"Runtime Calc result request failed with HTTP {(int)response.StatusCode}.";

            if (!string.IsNullOrWhiteSpace(responseText))
                message += $" Response: {LimitText(responseText, 500)}";

            throw new HttpRequestException(message, null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<CalcExecutionResultBatchResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty Calc result response.");
    }

    /// <summary>
    /// Выполняет один HTTP batch размером не более 500 тегов.
    /// </summary>
    private async Task<ScadaTagBatchReadResponse> ReadTagChunkAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct)
    {
        var request = new ScadaTagBatchReadRequest { TagNames = tagNames.ToList() };

        using var response = await httpClient.PostAsJsonAsync(
            "api/scada/tags/read-batch",
            request,
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(ct);
            var message = $"Runtime batch tag request failed with HTTP {(int)response.StatusCode}.";

            if (!string.IsNullOrWhiteSpace(responseText))
                message += $" Response: {LimitText(responseText, 500)}";

            throw new HttpRequestException(message, null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<ScadaTagBatchReadResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty SCADA batch response.");
    }

    /// <summary>
    /// Ограничивает размер диагностического текста HTTP-ошибки.
    /// </summary>
    private static string LimitText(string value, int maximumLength)
    {
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength] + "...";
    }

    /// <summary>
    /// Отправляет heartbeat и получает ownership/lease от Runtime.
    /// </summary>
    public async Task<CalcServiceHeartbeatResponseDto> SendHeartbeatAsync(CalcServiceHeartbeatRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await httpClient.PostAsJsonAsync(
            "api/calc/service/heartbeat",
            request,
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(ct);
            var message = $"Runtime Calc heartbeat request failed with HTTP {(int)response.StatusCode}.";

            if (!string.IsNullOrWhiteSpace(responseText))
                message += $" Response: {LimitText(responseText, 500)}";

            throw new HttpRequestException(message, null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<CalcServiceHeartbeatResponseDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty Calc Service heartbeat response.");
    }
}