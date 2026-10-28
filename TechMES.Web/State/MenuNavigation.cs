using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TechMES.Web.Settings;

namespace TechMES.Web.State;

/// <summary>
/// Навигация с сохранением профиля меню в URL.
/// Использует существующий NavigationManager; маршрутизация и NavigationLock остаются штатными.
/// </summary>
public sealed class MenuNavigation(NavigationManager navigation, IOptionsMonitor<MenuProfileOptions> options)
{
    public string Uri => navigation.Uri;

    /// <summary>
    /// Передаёт существующим подписчикам Equipment штатное событие навигации.
    /// </summary>
    public event EventHandler<LocationChangedEventArgs> LocationChanged
    {
        add => navigation.LocationChanged += value;
        remove => navigation.LocationChanged -= value;
    }

    public string ProfileName => ReadProfile(options.CurrentValue);

    /// <summary>
    /// Возвращает пункты выбранного профиля в установленном порядке главного меню.
    /// Неизвестные идентификаторы пунктов из конфигурации игнорируются.
    /// </summary>
    public IReadOnlyList<MainMenuItem> Items
    {
        get
        {
            var settings = options.CurrentValue;
            var profile = ReadProfile(settings);

            if (profile == "full")
                return MainMenuItems.All;

            var allowed = settings.Profiles.FirstOrDefault(pair => pair.Key.Equals(profile, StringComparison.OrdinalIgnoreCase)).Value ?? [];

            return MainMenuItems.All.Where(item => allowed.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
    }

    /// <summary>
    /// Без параметра, при пустом или неизвестном профиле используется полное меню.
    /// Регистр имени профиля не учитывается.
    /// </summary>
    private string ReadProfile(MenuProfileOptions settings)
    {
        var query = QueryHelpers.ParseQuery(new System.Uri(navigation.Uri).Query);

        if (!query.TryGetValue("menu", out var values) || values.Count != 1)
            return "full";

        var requested = values[0]?.Trim();

        if (string.Equals(requested, "full", StringComparison.OrdinalIgnoreCase))
            return "full";

        return settings.Profiles.Keys.FirstOrDefault(key => key.Equals(requested, StringComparison.OrdinalIgnoreCase)) ?? "full";
    }

    /// <summary>
    /// Добавляет текущий профиль к внутренней ссылке, сохраняя её параметры и fragment.
    /// Явный menu в новой ссылке имеет приоритет. Внешние адреса не изменяются.
    /// </summary>
    public string GetUri(string uri)
    {
        var destination = navigation.ToAbsoluteUri(uri);
        var appBase = new System.Uri(navigation.BaseUri);

        if (!appBase.IsBaseOf(destination) || QueryHelpers.ParseQuery(destination.Query).ContainsKey("menu"))
            return uri;

        // При работе без menu сохраняем прежние адреса без дополнительного параметра.
        if (!QueryHelpers.ParseQuery(new System.Uri(navigation.Uri).Query).ContainsKey("menu"))
            return uri;

        return QueryHelpers.AddQueryString(uri, "menu", ProfileName);
    }

    /// <summary>
    /// Выполняет один штатный переход сразу на адрес с профилем меню.
    /// Параметры forceLoad и replace сохраняют существующее поведение вызовов.
    /// </summary>
    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
    {
        navigation.NavigateTo(GetUri(uri), forceLoad, replace);
    }

    /// <summary>
    /// Сохраняет существующее чтение относительного адреса компонентами проекта.
    /// </summary>
    public string ToBaseRelativePath(string uri)
    {
        return navigation.ToBaseRelativePath(uri);
    }
}

/// <summary>
/// Определение пункта главного меню.
/// Для Scan QR вместо ссылки вызывается существующий обработчик.
/// </summary>
public sealed record MainMenuItem(string Id, string Title, string Icon, string Path);

/// <summary>
/// Единый порядок и оформление пунктов верхнего меню.
/// </summary>
public static class MainMenuItems
{
    public static IReadOnlyList<MainMenuItem> All { get; } = Array.AsReadOnly<MainMenuItem>(
    [
        new("equipment", "Equipment", "precision_manufacturing", "/equipment"),
        new("alarms", "Alarm", "notification_important", "/alarms"),
        new("events", "Events", "event_note", "/events"),
        new("soe", "SOE", "timeline", "/soe"),
        new("calculations", "Calculations", "calculate", "/calculations"),
        new("recipes", "Recipes", "menu_book", "/recipes"),
        new("messages", "Messages", "mail", "/messages"),
        new("scanqr", "Scan QR", "qr_code_scanner", "")
    ]);
}