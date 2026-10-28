namespace TechMES.Contracts.Security;

/// <summary>
/// Общий справочник ролей TechMES.
/// Порядок ролей используется для отображения и не означает иерархию прав.
/// </summary>
public static class AppRoles
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string Developer = "Developer";
    public const string Engineer = "Engineer";

    public static IReadOnlyList<AppRoleDefinition> All { get; } = Array.AsReadOnly<AppRoleDefinition>(
    [
        new(Operator, "Operator", "person"),
        new(Supervisor, "Supervisor", "account-tie"),
        new(Developer, "Developer", "code"),
        new(Engineer, "Engineer", "engineering")
    ]);

    /// <summary>
    /// Находит роль независимо от регистра и крайних пробелов.
    /// Неизвестное значение не преобразуется в другую роль.
    /// </summary>
    public static AppRoleDefinition? Find(string? id)
    {
        return All.FirstOrDefault(role => string.Equals(role.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Возвращает единое оформление роли для Header, Event Journal и Timeline.
    /// Старые события без роли и неизвестные значения получают нейтральную иконку.
    /// </summary>
    public static RoleDisplay GetDisplay(string? id)
    {
        var role = Find(id);
        var text = id?.Trim() ?? "";

        if (role is not null)
            return new RoleDisplay(role.Icon, role.Title, true);

        return new RoleDisplay("person", text.Length == 0 ? "Role not recorded" : $"Unknown role: {text}", false);
    }
}

/// <summary>
/// Постоянное определение роли приложения.
/// </summary>
public sealed record AppRoleDefinition(string Id, string Title, string Icon);

/// <summary>
/// Оформление известной, неизвестной или отсутствующей роли.
/// </summary>
public sealed record RoleDisplay(string Icon, string Title, bool IsKnown);