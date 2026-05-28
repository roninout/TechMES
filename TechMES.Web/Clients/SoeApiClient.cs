using System.Net.Http.Json;
using TechMES.Contracts.Soe;

namespace TechMES.Web.Clients;

public sealed class SoeApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SoeApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<SoeResponse> GetSoeAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("RuntimeService");
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<SoeResponse>(
            $"api/soe/{encodedName}",
            ct);

        return result ?? new SoeResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty SOE response."
        };
    }
}
