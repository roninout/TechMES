using TechMES.Contracts.Events;

namespace TechMES.Web.Components.Pages;

/// <summary>
/// Строка таблицы Operation actions, полученная из события категории Interface Event.
/// </summary>
public sealed class OperationActionRow
{
    public DateTime? Date { get; init; }
    public string User { get; init; } = "";
    public string Equipment { get; init; } = "";
    public string Action { get; init; } = "";
    public string Description { get; init; } = "";
    public string Old { get; init; } = "";
    public string New { get; init; } = "";
    public string ClientAddress { get; init; } = "";

    /// <summary>
    /// Разбирает сообщение вида Computer|User|Action [Description]|Old|New|Equipment.
    /// Если формат неизвестен, сохраняет исходное сообщение в видимой колонке Description.
    /// </summary>
    public static OperationActionRow FromEvent(EventJournalDto entry)
    {
        var fields = entry.Description.Split('|');

        if (fields.Length == 6 && TryParseAction(fields[2], out var action, out var description))
        {
            return new OperationActionRow
            {
                Date = entry.Date,
                User = fields[1].Trim(),
                Equipment = fields[5].Trim(),
                Action = action,
                Description = description,
                Old = fields[3].Trim(),
                New = fields[4].Trim(),
                ClientAddress = FormatClientAddress(fields[0], entry.ClientAddressDesc)
            };
        }

        return new OperationActionRow
        {
            Date = entry.Date,
            User = entry.User,
            Equipment = entry.Source,
            Description = entry.Description,
            ClientAddress = entry.ClientAddressDesc
        };
    }

    /// <summary>
    /// Отделяет название действия от описания в квадратных скобках.
    /// </summary>
    private static bool TryParseAction(string value, out string action, out string description)
    {
        action = "";
        description = "";

        var openingBracket = value.IndexOf('[');
        var closingBracket = value.LastIndexOf(']');

        if (openingBracket <= 0 || closingBracket <= openingBracket || closingBracket != value.Length - 1)
            return false;

        action = value[..openingBracket].Trim();
        description = value[(openingBracket + 1)..closingBracket].Trim();

        return !string.IsNullOrWhiteSpace(action);
    }

    /// <summary>
    /// Показывает имя компьютера вместе с адресом, который уже прочитан из журнала.
    /// </summary>
    private static string FormatClientAddress(string computer, string address)
    {
        computer = computer.Trim();
        address = address.Trim();

        if (string.IsNullOrEmpty(computer))
            return address;

        if (string.IsNullOrEmpty(address))
            return computer;

        return $"{computer} ({address})";
    }
}