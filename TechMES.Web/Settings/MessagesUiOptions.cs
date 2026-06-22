namespace TechMES.Web.Settings;

/// <summary>
/// UI-настройки страницы Messages.
/// Хранятся в appsettings.json, чтобы поведение страницы можно было менять без правки Razor-разметки.
/// </summary>
public sealed class MessagesUiOptions
{
    /// <summary>
    /// Показывать ли кнопку Delete в карточке выбранного сообщения.
    /// По умолчанию включено, чтобы текущее поведение приложения не менялось.
    /// </summary>
    public bool ShowDeleteButton { get; set; } = true;
}
