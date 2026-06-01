namespace TechMES.Contracts.Scada;

/// <summary>
/// Ответ на запись Plant SCADA tag.
/// </summary>
public sealed class ScadaTagWriteResponse
{
    /// <summary>
    /// Имя записанного tag-а.
    /// </summary>
    public string TagName { get; set; } = "";

    /// <summary>
    /// Значение, переданное в запись.
    /// </summary>
    public string? WrittenValue { get; set; }

    /// <summary>
    /// Успешно ли выполнена запись.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Текст ошибки, если запись не удалась.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Время выполнения записи.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;
}
