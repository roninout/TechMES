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
    public string TagName { get; set; } = "";

    public string? Value { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;
}