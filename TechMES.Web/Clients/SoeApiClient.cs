using System.Net.Http.Json;
using TechMES.Contracts.Soe;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для SOE-модуля.
/// </summary>
public sealed class SoeApiClient
{
    /// <summary>
    /// Фабрика именованного клиента RuntimeService.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Создает клиент SOE API.
    /// </summary>
    public SoeApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Загружает SOE-события для выбранного оборудования.
    /// </summary>
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
