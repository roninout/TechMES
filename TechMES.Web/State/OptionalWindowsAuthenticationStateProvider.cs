using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace TechMES.Web.State;

/// <summary>
/// Provides Windows authentication to Blazor as an optional identity.
/// If the client has selected read-only mode, the UI receives an anonymous principal
/// even when the browser can still answer the Negotiate challenge with cached credentials.
/// </summary>
public sealed class OptionalWindowsAuthenticationStateProvider
    : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
{
    /// <summary>
    /// Cookie used by the WEB app to keep the current browser session in read-only mode.
    /// This is intentionally a UI-level switch: Runtime.Service still makes the final write decision.
    /// </summary>
    public const string ReadOnlyCookieName = "TechMES.ReadOnlyMode";

    private static readonly AuthenticationState AnonymousState =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly IHttpContextAccessor _httpContextAccessor;
    private Task<AuthenticationState>? _hostAuthenticationStateTask;
    private bool _readOnlyMode;

    public OptionalWindowsAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Receives the authentication state created by the Blazor Server host for the current circuit.
    /// We keep that state, but can deliberately expose anonymous state when read-only mode is enabled.
    /// </summary>
    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        _readOnlyMode = IsReadOnlyRequest(_httpContextAccessor.HttpContext);
        _hostAuthenticationStateTask = authenticationStateTask;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>
    /// Returns the effective user for components and API clients.
    /// Anonymous users are allowed to browse, but Param write requests will be denied by Runtime.Service.
    /// </summary>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_readOnlyMode)
            return AnonymousState;

        if (_hostAuthenticationStateTask is null)
        {
            var httpUser = _httpContextAccessor.HttpContext?.User;
            return httpUser?.Identity?.IsAuthenticated == true
                ? new AuthenticationState(httpUser)
                : AnonymousState;
        }

        var hostState = await _hostAuthenticationStateTask;
        return hostState.User.Identity?.IsAuthenticated == true
            ? hostState
            : AnonymousState;
    }

    /// <summary>
    /// Checks whether this request should be treated as read-only by the WEB UI.
    /// </summary>
    public static bool IsReadOnlyRequest(HttpContext? httpContext)
    {
        return httpContext?.Request.Cookies.ContainsKey(ReadOnlyCookieName) == true;
    }
}
