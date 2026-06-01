namespace TechMES.Contracts.Param;

/// <summary>
/// Тип значения Param item-а.
/// </summary>
public enum ParamValueKind
{
    /// <summary>
    /// Тип не определен.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Boolean on/off.
    /// </summary>
    Boolean = 1,

    /// <summary>
    /// Числовое значение.
    /// </summary>
    Number = 2,

    /// <summary>
    /// Произвольный текст.
    /// </summary>
    Text = 3
}
