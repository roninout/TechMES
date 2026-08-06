using System.Net.Http.Json;
using System.Text.Json;
using TechMES.Contracts.Calc;

namespace TechMES.Calc.Service.Runtime;

/// <summary>
/// HTTP-реализация клиента Runtime.Service.
/// </summary>
public sealed class RuntimeCalcClient(HttpClient httpClient) : IRuntimeCalcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

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
    /// Ограничивает размер диагностического текста HTTP-ошибки.
    /// </summary>
    private static string LimitText(string value, int maximumLength)
    {
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength] + "...";
    }
}