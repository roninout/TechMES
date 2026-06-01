namespace TechMES.Contracts.Scada;

/// <summary>
/// Ответ на чтение одного Plant SCADA tag.
/// 
/// На первом этапе значение храним строкой,
/// потому что CtApi часто возвращает значения как string/object,
/// а типизацию лучше добавлять позже на уровне Param-моделей.
/// </summary>
public sealed class ScadaTagReadResponse
{
    /// <summary>
    /// Имя прочитанного tag-а.
    /// </summary>
    public string TagName { get; set; } = "";

    /// <summary>
    /// Значение tag-а как строка.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Успешно ли чтение.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Текст ошибки, если чтение не удалось.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Время чтения.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.Now;
}
