namespace TechMES.Web.Settings;

/// <summary>
/// Именованные наборы пунктов главного меню.
/// Выбор профиля передаётся в URL и не сохраняется в пользовательских настройках.
/// </summary>
public sealed class MenuProfileOptions
{
    public const string SectionName = "Menu";

    // Словарь заполняется из appsettings. Встроенный профиль full всегда означает все пункты.
    public Dictionary<string, string[]> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}