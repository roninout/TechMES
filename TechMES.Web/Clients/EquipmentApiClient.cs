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
    /// <summary>
    /// Фабрика именованного клиента RuntimeService.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Конфигурация WEB-проекта. Отсюда берется имя текущего клиента/устройства.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Создает API-клиент каталога оборудования.
    /// </summary>
    public EquipmentApiClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Имя WEB-клиента для favorite-флагов. Если в appsettings не задано, используем имя машины.
    /// </summary>
    public string DeviceName => _configuration["App:DeviceName"] ?? Environment.MachineName;

    /// <summary>
    /// Возвращает HttpClient, настроенный на Runtime.Service.
    /// </summary>
    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    /// <summary>
    /// Загружает каталог оборудования вместе с favorite-флагами и Info-счетчиками.
    /// </summary>
    public async Task<EquipmentListResponse> GetEquipmentListAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.GetFromJsonAsync<EquipmentListResponse>(
            $"api/equipment?deviceName={Uri.EscapeDataString(DeviceName)}",
            ct);

        return response ?? new EquipmentListResponse();
    }

    /// <summary>
    /// Загружает один equipment node по имени.
    /// </summary>
    public async Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default)
    {
        var client = CreateClient();

        var encodedName = Uri.EscapeDataString(name);

        return await client.GetFromJsonAsync<EquipmentDto>(
            $"api/equipment/{encodedName}?deviceName={Uri.EscapeDataString(DeviceName)}",
            ct);
    }

    /// <summary>
    /// Сохраняет favorite-флаг для текущего WEB-клиента.
    /// </summary>
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
