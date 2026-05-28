namespace TechMES.Contracts.EventLog;

public sealed class OperatorActionDto
{
    public DateTime Timestamp { get; set; }

    public string Time { get; set; } = "";

    public int Type { get; set; }

    public string Client { get; set; } = "";

    public string User { get; set; } = "";

    public string Tag { get; set; } = "";

    public string Equip { get; set; } = "";

    public string Desc { get; set; } = "";

    public string OldValue { get; set; } = "";

    public string NewValue { get; set; } = "";

    public string Page => ExtractPage(Desc);

    public string Comment => ExtractComment(Desc);

    private static string ExtractPage(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return "";

        var start = desc.IndexOf('[');
        var end = desc.IndexOf(']');

        return start >= 0 && end > start
            ? desc.Substring(start + 1, end - start - 1)
            : "";
    }

    private static string ExtractComment(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return "";

        var start = desc.IndexOf('[');
        var end = desc.IndexOf(']');

        return start >= 0 && end > start
            ? desc.Remove(start, end - start + 1).Trim()
            : desc.Trim();
    }
}
