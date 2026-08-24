namespace TechMES.Contracts.Param;

/// <summary>
/// Запрос проверки одного числового Plant SCADA тега.
///
/// Базовая операция проверяет, что тег существует и его текущее значение можно прочитать как число.
///
/// RequireTrend используется только тогда, когда вызывающему коду дополнительно нужен trend-reference.
///
/// Примеры:
///
/// Test Kp:
/// RequireTrend = false.
///
/// Density Temperature/Pressure при прямом Variable Tag:
/// RequireTrend = false.
///
/// PID Tune PV/SP:
/// RequireTrend = true.
/// </summary>
public sealed class ParamTagCheckRequest
{
    public string TagName { get; set; } = "";

    /// <summary>
    /// true, если кроме текущего числового значения тег обязательно должен иметь trend-reference.
    ///
    /// По умолчанию false, потому что обычная проверка числового online-тега не должна неожиданно требовать архив.
    /// </summary>
    public bool RequireTrend { get; set; }
}

/// <summary>
/// Результат общей проверки одного числового Plant SCADA тега.
///
/// DTO не привязан ни к PID Tune, ни к Density.
/// Его можно использовать в любом модуле, которому нужно проверить числовой Runtime tag.
/// </summary>
public sealed class ParamTagCheckResponse
{
    /// <summary>
    /// Нормализованное имя проверенного Variable Tag.
    /// </summary>
    public string TagName { get; set; } = "";

    /// <summary>
    /// Итог всей запрошенной проверки.
    ///
    /// Если TrendRequired = false:
    /// Found означает успешный numeric TagRead.
    ///
    /// Если TrendRequired = true:
    /// Found означает одновременно успешный numeric TagRead и найденный trend-reference.
    /// </summary>
    public bool Found { get; set; }

    /// <summary>
    /// Требовался ли trend-reference для данной проверки.
    /// </summary>
    public bool TrendRequired { get; set; }

    /// <summary>
    /// Найден ли trend-reference.
    ///
    /// Для обычного online tag при TrendRequired = false
    /// значение остаётся false и не влияет на Found.
    /// </summary>
    public bool TrendFound { get; set; }

    /// <summary>
    /// Текущее числовое значение тега, если TagRead
    /// успешно вернул и удалось разобрать число.
    /// </summary>
    public double? CurrentValue { get; set; }

    /// <summary>
    /// Диагностическое сообщение Runtime.
    /// </summary>
    public string? Message { get; set; }
}