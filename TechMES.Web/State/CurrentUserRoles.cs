using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using TechMES.Contracts.Security;
using TechMES.Web.Clients;

namespace TechMES.Web.State;

/// <summary>
/// Роли эффективного пользователя текущей Blazor-сессии.
/// На текущем этапе используются только для отображения иконок.
/// </summary>
public sealed class CurrentUserRoles(AuthenticationStateProvider authenticationStateProvider, RoleApiClient roleApi, ILogger<CurrentUserRoles> logger)
{
    public bool IsAuthenticated { get; private set; }
    public string? UserName { get; private set; }
    public IReadOnlyList<AppRoleDefinition> Roles { get; private set; } = [];
    public bool RolesUnavailable { get; private set; }

    /// <summary>
    /// Обновляет пользователя и его роли из существующего Blazor authentication state.
    /// При ошибке Runtime сохраняет факт входа, но не оставляет прежние роли.
    /// </summary>
    public async Task RefreshAsync()
    {
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;

        IsAuthenticated = principal.Identity?.IsAuthenticated == true;
        UserName = IsAuthenticated ? principal.Identity?.Name : null;
        Roles = [];
        RolesUnavailable = false;

        // Для анонимного пользователя или read-only режима не обращаемся к Runtime.
        if (!IsAuthenticated)
            return;

        try
        {
            // Недоступность Runtime не должна надолго задерживать открытие страницы.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Roles = await roleApi.ResolveAsync(principal, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            RolesUnavailable = true;
            logger.LogWarning(ex, "Cannot resolve display roles for the current WEB user.");
        }
    }
}