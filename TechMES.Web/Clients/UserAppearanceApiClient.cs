using System.Net.Http.Json;
using TechMES.Contracts.Users;
using TechMES.Web.State;

namespace TechMES.Web.Clients;

/// <summary>Использует существующий контекст пользователя; имя сервера владельцем не становится.</summary>
public sealed class UserAppearanceApiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly FavoriteOwnerContext _ownerContext;

    public UserAppearanceApiClient(IHttpClientFactory factory, FavoriteOwnerContext ownerContext)
    {
        _factory = factory;
        _ownerContext = ownerContext;
    }

    public async Task<UserAppearanceDto> GetAsync(CancellationToken ct = default)
    {
        var owner = await _ownerContext.GetCurrentAsync();

        if (!owner.IsAuthenticated)
            return new();

        var client = _factory.CreateClient("RuntimeService");
        var address = "api/user/appearance?userName=" + Uri.EscapeDataString(owner.OwnerKey);

        return await client.GetFromJsonAsync<UserAppearanceDto>(address, ct) ?? throw new InvalidOperationException("Appearance response is empty.");
    }

    public async Task SaveAsync(bool isDarkTheme, CancellationToken ct = default)
    {
        var owner = await _ownerContext.GetCurrentAsync();

        // В read-only режиме оформление меняется только в текущей сессии.
        if (!owner.IsAuthenticated) return;

        var client = _factory.CreateClient("RuntimeService");
        var address = "api/user/appearance?userName=" + Uri.EscapeDataString(owner.OwnerKey);

        using var response = await client.PutAsJsonAsync(address, new UserAppearanceDto { IsDarkTheme = isDarkTheme }, ct);
        response.EnsureSuccessStatusCode();
    }
}