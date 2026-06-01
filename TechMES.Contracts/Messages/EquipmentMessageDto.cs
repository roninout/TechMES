namespace TechMES.Contracts.Messages;

/// <summary>
/// DTO сообщения.
///
/// DTO (Data Transfer Object) — это объект для передачи данных между проектами.
/// В нашем случае Runtime Service возвращает такой объект через HTTP API,
/// а TechMES.Web получает его и отображает на странице Messages.
/// </summary>
public sealed class EquipmentMessageDto
{
    /// <summary>
    /// Идентификатор сообщения.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Тип сообщения.
    /// </summary>
    public MessageType MessageType { get; set; } = MessageType.Info;

    /// <summary>
    /// Тема сообщения.
    /// </summary>
    public string MessageSubject { get; set; } = string.Empty;

    /// <summary>
    /// Основной текст сообщения.
    /// </summary>
    public string MessageText { get; set; } = string.Empty;

    /// <summary>
    /// Активно ли сообщение.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Устройство/пользователь, создавший сообщение.
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Дата создания.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Кто последним изменил сообщение.
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Дата последнего изменения.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Просмотрело ли это сообщение текущее устройство.
    /// </summary>
    public bool IsViewedByCurrentDevice { get; set; }

    /// <summary>
    /// Просмотрел ли это сообщение кто-то кроме автора.
    /// </summary>
    public bool IsViewedByOtherDevice { get; set; }

    /// <summary>
    /// Список устройств, просмотревших сообщение.
    /// </summary>
    public string ViewedByText { get; set; } = string.Empty;

    /// <summary>
    /// Короткий текст для карточки в списке сообщений.
    /// Это вычисляемое свойство удобно держать рядом с DTO,
    /// потому что оно одинаково полезно для WEB и для других клиентов.
    /// </summary>
    public string PreviewText
    {
        get
        {
            var text = (MessageText ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "Empty message";

            text = text.Replace("\r", " ").Replace("\n", " ");

            return text.Length <= 60
                ? text
                : text[..60] + "...";
        }
    }
}
