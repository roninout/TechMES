namespace TechMES.Contracts.Param;

/// <summary>
/// Вкладки Param-модуля, которые может поддерживать тип оборудования.
/// </summary>
public enum ParamPageKind
{
    /// <summary>
    /// График тренда.
    /// </summary>
    Chart = 0,

    /// <summary>
    /// Текущие значения.
    /// </summary>
    Values = 1,

    /// <summary>
    /// PLC reference page.
    /// </summary>
    Plc = 2,

    /// <summary>
    /// DI/DO reference page.
    /// </summary>
    DiDo = 3,

    /// <summary>
    /// Alarm-параметры.
    /// </summary>
    Alarm = 4,

    /// <summary>
    /// Время работы.
    /// </summary>
    TimeWork = 5,

    /// <summary>
    /// DryRun reference page.
    /// </summary>
    DryRun = 6,

    /// <summary>
    /// ATV reference page.
    /// </summary>
    Atv = 7
}
