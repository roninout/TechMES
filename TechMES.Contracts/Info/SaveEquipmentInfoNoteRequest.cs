namespace TechMES.Contracts.Info;

/// <summary>
/// Запрос создания или редактирования заметки.
/// </summary>
public sealed class SaveEquipmentInfoNoteRequest
{
    /// <summary>
    /// Текст заметки.
    /// </summary>
    public string NoteText { get; set; } = string.Empty;
}
