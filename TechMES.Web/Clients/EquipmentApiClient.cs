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
    private readonly IConfiguration _configuration;

    public EquipmentApiClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string DeviceName => _configuration["App:DeviceName"] ?? Environment.MachineName;

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    public async Task<EquipmentListResponse> GetEquipmentListAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.GetFromJsonAsync<EquipmentListResponse>(
            $"api/equipment?deviceName={Uri.EscapeDataString(DeviceName)}",
            ct);

        return response ?? new EquipmentListResponse();
    }

    public async Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default)
    {
        var client = CreateClient();

        var encodedName = Uri.EscapeDataString(name);

        return await client.GetFromJsonAsync<EquipmentDto>(
            $"api/equipment/{encodedName}?deviceName={Uri.EscapeDataString(DeviceName)}",
            ct);
    }

    public async Task<EquipmentDto?> SetFavoriteAsync(string name, bool isFavorite, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(name);

        var response = await client.PutAsJsonAsync(
            $"api/equipment/{encodedName}/favorite?deviceName={Uri.EscapeDataString(DeviceName)}",
            new { IsFavorite = isFavorite },
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<EquipmentDto>(cancellationToken: ct);
    }
}
