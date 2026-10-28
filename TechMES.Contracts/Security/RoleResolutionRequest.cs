namespace TechMES.Contracts.Security;

/// <summary>
/// Windows-группы пользователя, прочитанные серверным WEB-клиентом.
/// Runtime использует их для определения отображаемых ролей.
/// </summary>
public sealed class RoleResolutionRequest
{
    public List<string> Groups { get; set; } = [];
}
