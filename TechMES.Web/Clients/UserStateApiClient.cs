using System.Net.Http.Json;
using System.Text.Json;
using TechMES.Contracts.Users;
using TechMES.Web.State;

namespace TechMES.Web.Clients;

/// <summary>Владелец определяется существующим FavoriteOwnerContext, а не именем WEB-сервера.</summary>
public sealed class UserStateApiClient(IHttpClientFactory factory, FavoriteOwnerContext ownerContext)
{
    public async Task<UserStateDto> GetAsync(CancellationToken ct = default)
    {
        var owner = await ownerContext.GetCurrentAsync();
        if (!owner.IsAuthenticated) return new();

        var client = factory.CreateClient("RuntimeService");

        return await client.GetFromJsonAsync<UserStateDto>("api/user/state?userName=" + Uri.EscapeDataString(owner.OwnerKey), ct) ?? throw new InvalidOperationException("User state response is empty.");
    }

    public async Task PatchAsync(JsonElement changes, CancellationToken ct = default)
    {
        var owner = await ownerContext.GetCurrentAsync();
        if (!owner.IsAuthenticated) return;

        var client = factory.CreateClient("RuntimeService");

        using var request = new HttpRequestMessage(HttpMethod.Patch, "api/user/state?userName=" + Uri.EscapeDataString(owner.OwnerKey)) { Content = JsonContent.Create(changes) };
        using var response = await client.SendAsync(request, ct);

        response.EnsureSuccessStatusCode();
    }
}