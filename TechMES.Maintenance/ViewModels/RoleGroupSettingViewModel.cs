using TechMES.Contracts.Security;

namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Строка настройки привязки глобальной роли к Windows-группам.
/// </summary>
public sealed class RoleGroupSettingViewModel(AppRoleDefinition role) : ObservableObject
{
    private string _allowedGroups = "";

    public string Id => role.Id;
    public string Title => role.Title;

    /// <summary>
    /// Windows-группы роли через точку с запятой или запятую.
    /// </summary>
    public string AllowedGroups
    {
        get => _allowedGroups;
        set => SetProperty(ref _allowedGroups, value);
    }
}