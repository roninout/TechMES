namespace TechMES.Contracts.Info;

public sealed class EquipmentInfoNoteDto
{
    public long Id { get; set; }

    public string EquipName { get; set; } = string.Empty;

    public string NoteText { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

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
