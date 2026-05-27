using System.Net.Http.Json;
using TechMES.Contracts.Param;

namespace TechMES.Web.Clients;

public sealed class ParamApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ParamApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ParamSnapshotResponse> GetSnapshotAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamSnapshotResponse>(
            $"api/param/{encodedName}/snapshot",
            ct);

        return result ?? new ParamSnapshotResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty Param snapshot."
        };
    }

    public async Task<ParamTrendResponse> GetTrendAsync(
        string equipmentName,
        int windowMinutes,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);
        var query = $"windowMinutes={windowMinutes}";

        if (fromUtc.HasValue)
            query += $"&fromUtc={Uri.EscapeDataString(ToQueryUtc(fromUtc.Value))}";

        if (toUtc.HasValue)
            query += $"&toUtc={Uri.EscapeDataString(ToQueryUtc(toUtc.Value))}";

        var result = await client.GetFromJsonAsync<ParamTrendResponse>(
            $"api/param/{encodedName}/trend?{query}",
            ct);

        return result ?? new ParamTrendResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty Param trend."
        };
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    private static string ToQueryUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

        return utc.ToString("O");
    }
}
