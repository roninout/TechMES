namespace TechMES.Calc.Service.Settings;

/// <summary>
/// Настройки планировщика и shadow-выполнения расчётов.
/// </summary>
public sealed class CalcExecutionOptions
{
    /// <summary>
    /// Частота проверки заданий, срок выполнения которых наступил.
    ///
    /// Это не период самого расчёта. Период каждого job хранится
    /// отдельно в CalcExecutionJobDto.PeriodMs.
    /// </summary>
    public int SchedulerTickMilliseconds { get; set; } = 250;

    /// <summary>
    /// Максимальный возраст входного значения по умолчанию.
    ///
    /// Индивидуальный MaxAgeSeconds входа имеет больший приоритет.
    /// </summary>
    public int DefaultMaxAgeSeconds { get; set; } = 30;

    /// <summary>
    /// Разрешает использовать Quality=Unknown.
    ///
    /// Пока включено, потому что текущий CtApi wrapper не возвращает
    /// нативное качество тега. Quality=Bad и Uncertain не принимаются.
    /// </summary>
    public bool AcceptUnknownQuality { get; set; } = true;
}