namespace TechMES.Contracts.Scada;

/// <summary>
/// Ответ на запись Plant SCADA tag.
/// </summary>
public sealed class ScadaTagWriteResponse
{
    public string TagName { get; set; } = "";

    public string? WrittenValue { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;
}