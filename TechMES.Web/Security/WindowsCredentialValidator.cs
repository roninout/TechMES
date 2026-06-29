using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Win32.SafeHandles;

namespace TechMES.Web.Security;

#pragma warning disable CA1416

/// <summary>
/// Validates WEB login credentials against the local Windows machine or domain.
/// This replaces browser Negotiate prompts with an explicit TechMES login form.
/// </summary>
public sealed class WindowsCredentialValidator
{
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    /// <summary>
    /// Checks the supplied credentials and returns a cookie-ready ClaimsPrincipal.
    /// </summary>
    public WindowsCredentialValidationResult Validate(string? userNameText, string? password)
    {
        if (!OperatingSystem.IsWindows())
            return WindowsCredentialValidationResult.Fail("Windows credential validation is available only on Windows.");

        var parsed = ParseUserName(userNameText);
        if (string.IsNullOrWhiteSpace(parsed.UserName))
            return WindowsCredentialValidationResult.Fail("User name is empty.");

        if (string.IsNullOrEmpty(password))
            return WindowsCredentialValidationResult.Fail("Password is empty.");

        if (!LogonUser(
                parsed.UserName,
                parsed.Domain,
                password,
                Logon32LogonInteractive,
                Logon32ProviderDefault,
                out var token))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return WindowsCredentialValidationResult.Fail(error);
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var claims = BuildClaims(identity);
            var cookieIdentity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimTypes.Name,
                ClaimTypes.Role);

            return WindowsCredentialValidationResult.Ok(new ClaimsPrincipal(cookieIdentity));
        }
    }

    /// <summary>
    /// Parses WEB form input into Windows LogonUser arguments.
    /// Short names are treated as local machine accounts.
    /// </summary>
    private static (string UserName, string? Domain) ParseUserName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return ("", null);

        var slashIndex = trimmed.IndexOf('\\');
        if (slashIndex > 0 && slashIndex < trimmed.Length - 1)
        {
            var domain = trimmed[..slashIndex];
            var userName = trimmed[(slashIndex + 1)..];

            if (domain == ".")
                domain = Environment.MachineName;

            return (userName, domain);
        }

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? (trimmed, null)
            : (trimmed, Environment.MachineName);
    }

    /// <summary>
    /// Converts WindowsIdentity into stable claims used by ParamApiClient and Runtime.Service authorization.
    /// </summary>
    private static List<Claim> BuildClaims(WindowsIdentity identity)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.Name),
            new(ClaimTypes.NameIdentifier, identity.User?.Value ?? identity.Name)
        };

        if (identity.User is not null)
            claims.Add(new Claim(ClaimTypes.PrimarySid, identity.User.Value));

        if (identity.Groups is null)
            return claims;

        foreach (var group in identity.Groups)
        {
            claims.Add(new Claim(ClaimTypes.GroupSid, group.Value));

            var translated = TryTranslateIdentityReference(group);
            if (!string.IsNullOrWhiteSpace(translated))
                claims.Add(new Claim(ClaimTypes.Role, translated));
        }

        return claims;
    }

    /// <summary>
    /// Translates a SID to DOMAIN\Name when the current machine can resolve it.
    /// </summary>
    private static string? TryTranslateIdentityReference(IdentityReference reference)
    {
        try
        {
            return reference.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return reference.Value;
        }
        catch (SystemException)
        {
            return reference.Value;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out SafeAccessTokenHandle phToken);
}

#pragma warning restore CA1416
