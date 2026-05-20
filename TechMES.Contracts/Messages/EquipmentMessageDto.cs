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
    public long Id { get; set; }

    public MessageType MessageType { get; set; } = MessageType.Info;

    public string MessageSubject { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public bool IsViewedByCurrentDevice { get; set; }

    public bool IsViewedByOtherDevice { get; set; }

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
