using System.Security.Claims;
using TechMES.Contracts.Security;
using TechMES.Web.Security;

namespace TechMES.Web.Clients;

/// <summary>
/// Получает роли пользователя через единственную конфигурацию Runtime.Service.
/// </summary>
public sealed class RoleApiClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>
    /// Передаёт Runtime реальные Windows-группы текущего пользователя.
    /// Возвращает определения ролей из общего справочника.
    /// </summary>
    public async Task<IReadOnlyList<AppRoleDefinition>> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return [];

        var request = new RoleResolutionRequest
        {
            Groups = WindowsPrincipalGroups.Read(principal).Where(group => !string.IsNullOrWhiteSpace(group)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        using var client = httpClientFactory.CreateClient("RuntimeService");
        using var response = await client.PostAsJsonAsync("api/security/roles/resolve", request, ct);

        response.EnsureSuccessStatusCode();

        var ids = await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: ct) ?? throw new HttpRequestException("Runtime returned an empty role response.");

        return AppRoles.All.Where(role => ids.Contains(role.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
