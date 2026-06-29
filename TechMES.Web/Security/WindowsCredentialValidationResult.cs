using System.Security.Claims;

namespace TechMES.Web.Security;

/// <summary>
/// Result of checking a username/password pair against Windows.
/// The WEB application stores the returned ClaimsPrincipal in an auth cookie.
/// </summary>
public sealed class WindowsCredentialValidationResult
{
    private WindowsCredentialValidationResult(bool success, ClaimsPrincipal? principal, string? error)
    {
        Success = success;
        Principal = principal;
        Error = error;
    }

    /// <summary>
    /// True when Windows accepted the supplied credentials.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Principal with Windows user name and translated Windows groups.
    /// </summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>
    /// Human-readable validation error for the login page.
    /// </summary>
    public string? Error { get; }

    public static WindowsCredentialValidationResult Ok(ClaimsPrincipal principal)
    {
        return new WindowsCredentialValidationResult(true, principal, null);
    }

    public static WindowsCredentialValidationResult Fail(string error)
    {
        return new WindowsCredentialValidationResult(false, null, error);
    }
}
