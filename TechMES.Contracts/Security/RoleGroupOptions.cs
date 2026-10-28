namespace TechMES.Contracts.Security;

/// <summary>
/// Привязки ролей TechMES к Windows-группам.
/// Хранятся в Runtime appsettings и сами по себе не разрешают запись параметров.
/// </summary>
public sealed class RoleGroupOptions
{
    public const string SectionName = "Roles";

    public Dictionary<string, string> AllowedGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Читает список групп роли независимо от регистра ключа конфигурации.
    /// Отсутствующая привязка означает, что роль не назначается.
    /// </summary>
    public string GetAllowedGroups(string roleId)
    {
        return AllowedGroups.FirstOrDefault(pair => pair.Key.Equals(roleId, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    }

    /// <summary>
    /// Определяет все роли по Windows-группам пользователя.
    /// Неизвестные роли из конфигурации игнорируются.
    /// </summary>
    public List<string> Resolve(IEnumerable<string> windowsGroups)
    {
        var actualGroups = windowsGroups.Where(group => !string.IsNullOrWhiteSpace(group)).ToArray();

        return AppRoles.All.Where(role => SplitGroups(GetAllowedGroups(role.Id)).Any(expected => actualGroups.Any(actual => GroupMatches(actual, expected)))).Select(role => role.Id).ToList();
    }

    /// <summary>
    /// Разбирает список групп через точку с запятой или запятую.
    /// Удаляет пустые значения, крайние пробелы и повторения.
    /// </summary>
    public static string[] SplitGroups(string? value)
    {
        return (value ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Полное имя DOMAIN\Group требует совпадения домена или компьютера.
    /// Короткое имя Group допускает совпадение группы по короткому имени.
    /// </summary>
    private static bool GroupMatches(string actual, string expected)
    {
        actual = actual.Trim();

        if (expected.Contains('\\'))
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

        var slash = actual.LastIndexOf('\\');
        return actual[(slash + 1)..].Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}