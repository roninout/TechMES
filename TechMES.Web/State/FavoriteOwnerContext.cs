using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace TechMES.Web.State;

/// <summary>
/// Определяет владельца Favorites для текущей Blazor Server-сессии.
///
/// Важно:
/// WEB выполняется на сервере, поэтому Environment.MachineName всегда будет именем сервера.
/// Для персонального избранного нужно использовать Windows identity текущего пользователя,
/// который вошел через TechMES login / Windows authentication.
/// </summary>
public sealed class FavoriteOwnerContext
{
    /// <summary>
    /// Служебный owner для read-only / anonymous режима.
    /// Для него Favorites можно читать как пустой набор, но нельзя записывать.
    /// </summary>
    public const string AnonymousOwnerKey = "__anonymous__";

    private readonly AuthenticationStateProvider _authenticationStateProvider;

    public FavoriteOwnerContext(AuthenticationStateProvider authenticationStateProvider)
    {
        _authenticationStateProvider = authenticationStateProvider;
    }

    /// <summary>
    /// Возвращает текущего владельца Favorites.
    /// </summary>
    public async Task<FavoriteOwnerState> GetCurrentAsync()
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authenticationState.User;

        var identity = user.Identity;
        if (identity?.IsAuthenticated != true)
        {
            return FavoriteOwnerState.Anonymous;
        }

        var userName =
            identity.Name
            ?? user.FindFirst(ClaimTypes.Name)?.Value
            ?? "";

        userName = NormalizeUserName(userName);

        if (string.IsNullOrWhiteSpace(userName))
        {
            return FavoriteOwnerState.Anonymous;
        }

        return new FavoriteOwnerState(
            OwnerKey: userName,
            DisplayName: userName,
            IsAuthenticated: true,
            CanEditFavorites: true);
    }

    private static string NormalizeUserName(string? value)
    {
        value = (value ?? "").Trim();

        if (string.IsNullOrWhiteSpace(value))
            return "";

        // Ограничиваем длину ключа, чтобы случайно не записать в БД огромную строку.
        return value.Length <= 256
            ? value
            : value[..256];
    }
}

/// <summary>
/// Состояние владельца Favorites для текущего пользователя WEB.
/// </summary>
public sealed record FavoriteOwnerState(
    string OwnerKey,
    string DisplayName,
    bool IsAuthenticated,
    bool CanEditFavorites)
{
    public static FavoriteOwnerState Anonymous { get; } = new(
        OwnerKey: FavoriteOwnerContext.AnonymousOwnerKey,
        DisplayName: "Read-only",
        IsAuthenticated: false,
        CanEditFavorites: false);
}