using System.Net.Http.Json;
using TechMES.Contracts.Equipment;
using TechMES.Web.State;

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
    /// Контекст текущего Windows-пользователя для персонального Favorites.
    /// </summary>
    private readonly FavoriteOwnerContext _favoriteOwnerContext;

    /// <summary>
    /// Создает API-клиент каталога оборудования.
    /// </summary>
    public EquipmentApiClient(
        IHttpClientFactory httpClientFactory,
        FavoriteOwnerContext favoriteOwnerContext)
    {
        _httpClientFactory = httpClientFactory;
        _favoriteOwnerContext = favoriteOwnerContext;
    }

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
        var favoriteOwner = await _favoriteOwnerContext.GetCurrentAsync();

        var response = await client.GetFromJsonAsync<EquipmentListResponse>(
            $"api/equipment?{BuildFavoriteOwnerQuery(favoriteOwner)}",
            ct);

        return response ?? new EquipmentListResponse();
    }

    /// <summary>
    /// Загружает один equipment node по имени.
    /// </summary>
    public async Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default)
    {
        var client = CreateClient();
        var favoriteOwner = await _favoriteOwnerContext.GetCurrentAsync();

        var encodedName = Uri.EscapeDataString(name);

        return await client.GetFromJsonAsync<EquipmentDto>(
            $"api/equipment/{encodedName}?{BuildFavoriteOwnerQuery(favoriteOwner)}",
            ct);
    }

    /// <summary>
    /// Сохраняет favorite-флаг для текущего Windows-пользователя.
    /// В read-only / anonymous режиме запись запрещена.
    /// </summary>
    public async Task<EquipmentDto?> SetFavoriteAsync(
        string name,
        bool isFavorite,
        CancellationToken ct = default)
    {
        var favoriteOwner = await _favoriteOwnerContext.GetCurrentAsync();

        if (!favoriteOwner.CanEditFavorites)
        {
            throw new InvalidOperationException("Windows login is required to use Favorites.");
        }

        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(name);

        var response = await client.PutAsJsonAsync(
            $"api/equipment/{encodedName}/favorite?{BuildFavoriteOwnerQuery(favoriteOwner)}",
            new { IsFavorite = isFavorite },
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<EquipmentDto>(cancellationToken: ct);
    }

    /// <summary>
    /// Runtime.Service уже принимает новый параметр userName.
    /// Старый query-параметр deviceName больше не используется WEB-клиентом.
    /// </summary>
    private static string BuildFavoriteOwnerQuery(FavoriteOwnerState favoriteOwner)
    {
        return "userName=" + Uri.EscapeDataString(favoriteOwner.OwnerKey);
    }
}