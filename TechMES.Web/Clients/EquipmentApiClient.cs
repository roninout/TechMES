using System.Net.Http.Json;
using TechMES.Contracts.Equipment;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент для работы с каталогом оборудования.
/// 
/// WEB не знает, откуда Runtime.Service берёт оборудование:
/// InMemory, CtApi, PostgreSQL или другой источник.
/// WEB просто вызывает /api/equipment.
/// </summary>
public sealed class EquipmentApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public EquipmentApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    public async Task<EquipmentListResponse> GetEquipmentListAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.GetFromJsonAsync<EquipmentListResponse>("api/equipment", ct);

        return response ?? new EquipmentListResponse();
    }

    public async Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default)
    {
        var client = CreateClient();

        var encodedName = Uri.EscapeDataString(name);

        return await client.GetFromJsonAsync<EquipmentDto>($"api/equipment/{encodedName}", ct);
    }
}