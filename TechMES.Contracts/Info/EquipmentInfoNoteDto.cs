namespace TechMES.Contracts.Info;

/// <summary>
/// Заметка Info-модуля, привязанная к оборудованию.
/// </summary>
public sealed class EquipmentInfoNoteDto
{
    /// <summary>
    /// Идентификатор заметки.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string EquipName { get; set; } = string.Empty;

    /// <summary>
    /// Полный текст заметки.
    /// </summary>
    public string NoteText { get; set; } = string.Empty;

    /// <summary>
    /// Автор создания.
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Дата создания.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Автор последнего изменения.
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Дата последнего изменения.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Короткий текст для карточки в списке заметок.
    /// </summary>
    public string PreviewText
    {
        get
        {
            var text = (NoteText ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "Empty note";

            text = text.Replace("\r", " ").Replace("\n", " ");

            return text.Length <= 60
                ? text
                : text[..60] + "...";
        }
    }
}
