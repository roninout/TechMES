using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using TechMES.Contracts.Security;
using TechMES.Web.Clients;

namespace TechMES.Web.State;

/// <summary>
/// Роли эффективного пользователя текущей Blazor-сессии.
/// Используются для отображения иконок и доступных элементов интерфейса.
/// </summary>
public sealed class CurrentUserRoles(AuthenticationStateProvider authenticationStateProvider, RoleApiClient roleApi, ILogger<CurrentUserRoles> logger)
{
    private Task? _loadTask;

    public bool IsAuthenticated { get; private set; }
    public string? UserName { get; private set; }
    public IReadOnlyList<AppRoleDefinition> Roles { get; private set; } = [];
    public bool RolesUnavailable { get; private set; }

    /// <summary>
    /// Проверяет роль текущего пользователя.
    /// При ошибке чтения ролей доступные только по роли элементы скрываются.
    /// </summary>
    public bool HasRole(string roleId)
    {
        return IsAuthenticated && !RolesUnavailable && Roles.Any(role => role.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ожидает первоначальную загрузку ролей.
    /// Header и компоненты страницы используют один запрос.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        return _loadTask ??= LoadAsync();
    }

    /// <summary>
    /// Явно перечитывает роли, например перед открытием диалога Authentication.
    /// </summary>
    public async Task RefreshAsync()
    {
        _loadTask = LoadAsync();
        await _loadTask;
    }

    /// <summary>
    /// Читает эффективного пользователя и его роли.
    /// При недоступности Runtime сохраняет вход, но не оставляет прежние роли.
    /// </summary>
    private async Task LoadAsync()
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
