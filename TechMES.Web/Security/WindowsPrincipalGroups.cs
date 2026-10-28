using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace TechMES.Web.Security;

/// <summary>
/// Общее чтение Windows-групп для отображаемых ролей и существующего Param write-flow.
/// </summary>
public static class WindowsPrincipalGroups
{
    /// <summary>
    /// Собирает группы из WindowsIdentity и cookie claims.
    /// SID переводятся в DOMAIN\Group, если Windows может разрешить их имена.
    /// </summary>
    public static IEnumerable<string> Read(ClaimsPrincipal? principal)
    {
        if (OperatingSystem.IsWindows() && principal?.Identity is WindowsIdentity windowsIdentity)
        {
            foreach (var group in ReadWindowsIdentityGroups(windowsIdentity))
                yield return group;
        }

        if (principal is null)
            yield break;

        foreach (var claim in principal.Claims)
        {
            if (claim.Type == ClaimTypes.Role || claim.Type == ClaimTypes.GroupSid || claim.Type.EndsWith("/groupsid", StringComparison.OrdinalIgnoreCase))
            {
                var translated = OperatingSystem.IsWindows() ? TryTranslateSid(claim.Value) : null;
                yield return translated ?? claim.Value;
            }
        }
    }

    /// <summary>
    /// Читает группы WindowsIdentity, допуская SID, которые не удалось разрешить.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadWindowsIdentityGroups(WindowsIdentity windowsIdentity)
    {
        if (windowsIdentity.Groups is null)
            yield break;

        foreach (var group in windowsIdentity.Groups)
        {
            var translated = TryTranslateIdentityReference(group);

            if (!string.IsNullOrWhiteSpace(translated))
                yield return translated;
        }
    }

    /// <summary>
    /// Переводит строковый SID в читаемое имя Windows-группы.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryTranslateSid(string value)
    {
        try
        {
            return new SecurityIdentifier(value).Translate(typeof(NTAccount)).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// Переводит Windows IdentityReference в имя группы.
    /// Если перевод невозможен, сохраняет исходный SID.
    /// </summary>
    [SupportedOSPlatform("windows")]
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
}
