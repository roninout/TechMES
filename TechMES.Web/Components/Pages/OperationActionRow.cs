using System.Text.RegularExpressions;
using TechMES.Contracts.Events;

namespace TechMES.Web.Components.Pages;

/// <summary>
/// Строка таблицы Operation actions, полученная из события категории Interface Event.
/// </summary>
public sealed class OperationActionRow
{
    public DateTime? Date { get; init; }
    public string User { get; init; } = "";
    public string Role { get; init; } = "";
    public string Equipment { get; init; } = "";
    public string Action { get; init; } = "";
    public string Description { get; init; } = "";
    public string Old { get; init; } = "";
    public string New { get; init; } = "";
    public string Client { get; init; } = "";
    public string Address { get; init; } = "";

    /// <summary>
    /// Разбирает Message формата Computer|User|Action [Description]|Old|New|Equipment|Role.
    /// Поддерживает прежние сообщения из шести полей, в которых Role отсутствует.
    /// Неизвестный формат оставляет видимым как исходный текст в Description.
    /// </summary>
    public static OperationActionRow FromEvent(EventJournalDto entry)
    {
        var fields = entry.Description.Split('|');

        if ((fields.Length == 6 || fields.Length == 7) && TryParseAction(fields[2], out var action, out var description))
        {
            return new OperationActionRow
            {
                Date = entry.Date,
                User = EventJournalDisplayText.Clean(fields[1]),
                Role = fields.Length == 7 ? EventJournalDisplayText.Clean(fields[6]) : "",
                Equipment = fields[5].Trim(),
                Action = action,
                Description = description,
                Old = fields[3].Trim(),
                New = fields[4].Trim(),
                Client = fields[0].Trim(),
                Address = entry.ClientAddressDesc
            };
        }

        return new OperationActionRow
        {
            Date = entry.Date,
            User = EventJournalDisplayText.Clean(entry.User),
            Equipment = entry.Source,
            Description = EventJournalDisplayText.Clean(entry.Description),
            Address = entry.ClientAddressDesc
        };
    }

    /// <summary>
    /// Отделяет название действия от описания в квадратных скобках.
    /// </summary>
    private static bool TryParseAction(string value, out string action, out string description)
    {
        action = "";
        description = "";
        value = value.Trim();

        var openingBracket = value.IndexOf('[');
        var closingBracket = value.LastIndexOf(']');

        if (openingBracket <= 0 || closingBracket <= openingBracket || closingBracket != value.Length - 1)
            return false;

        action = EventJournalDisplayText.Clean(value[..openingBracket]);
        description = EventJournalDisplayText.Clean(value[(openingBracket + 1)..closingBracket]);

        return !string.IsNullOrWhiteSpace(action);
    }
}

/// <summary>
/// Готовит текст журнала для отображения: снимает оболочку @(...) и удаляет символ экранирования ^.
/// Обычные круглые скобки, не начинающиеся с @(, сохраняет.
/// </summary>
internal static class EventJournalDisplayText
{
    private static readonly Regex WrappedText = new(@"@\(([^()]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Очищает служебное оформление Plant SCADA и крайние пробелы.
    /// </summary>
    public static string Clean(string? value)
    {
        var text = value?.Trim() ?? "";

        if (text.Length == 0)
            return "";

        text = text.Replace("^", "", StringComparison.Ordinal);
        return WrappedText.Replace(text, "$1").Trim();
    }
}