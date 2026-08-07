namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Единый набор численных критериев автоматической идентификации PID Tune.
///
/// Эти значения относятся к реализации TechMES и используются одновременно
/// расчетным ядром и справкой WEB. Это не универсальные физические константы:
/// они задают консервативные критерии приемки выбранного участка тренда.
/// </summary>
public static class PidTuneIdentificationRules
{
    /// <summary>
    /// Мгновенный скачок OUT должен составлять не менее этой доли
    /// устойчивой разницы уровней до/после ступени.
    /// </summary>
    public const double MinimumStepInstantFraction = 0.50;

    /// <summary>
    /// Медиана хвоста OUT должна находиться не дальше этой доли DeltaOUT
    /// от найденного нового уровня.
    /// </summary>
    public const double MaximumStepTailLevelErrorRatio = 0.20;

    /// <summary>
    /// Робастный разброс P95-P05 последних точек OUT
    /// должен быть не более этой доли |DeltaOUT|.
    /// </summary>
    public const double MaximumOutputTailRangeRatio = 0.15;

    /// <summary>
    /// Минимальный R² fitted FOPDT-модели.
    /// </summary>
    public const double MinimumFopdtR2 = 0.65;

    /// <summary>
    /// Минимальная наблюдаемая доля полного FOPDT-отклика.
    ///
    /// 1-exp(-1)=0.632120... соответствует одной Tau после окончания Theta.
    /// Это критерий достаточной наблюдаемости Tau/K, а не способ расчета Tau.
    /// </summary>
    public const double MinimumFopdtObservedResponseFraction =
        0.6321205588285577;

    /// <summary>
    /// Полная fitted-амплитуда FOPDT должна превышать шум исходного PV
    /// минимум во столько стандартных отклонений.
    /// </summary>
    public const double MinimumFopdtSignalToNoiseSigma = 3.0;

    /// <summary>
    /// Минимальный R² кусочно-линейной Integrating-модели.
    /// </summary>
    public const double MinimumIntegratingR2 = 0.70;

    /// <summary>
    /// Эффект изменения наклона Integrating-модели за post-window
    /// должен превышать исходный PV-шум минимум во столько сигм.
    /// </summary>
    public const double MinimumIntegratingSlopeSignalToNoiseSigma = 3.0;

    /// <summary>
    /// Минимальное количество полных периодов для ClosedLoop.
    /// </summary>
    public const int MinimumClosedLoopCycles = 4;

    /// <summary>
    /// Гистерезис crossing detector как доля робастной амплитуды ошибки PV-SP.
    /// </summary>
    public const double ClosedLoopCrossingHysteresisFraction = 0.15;

    /// <summary>
    /// Периоды, отличающиеся от исходной медианы сильнее этого нижнего отношения,
    /// считаются единичными ложными/пропущенными crossing.
    /// </summary>
    public const double ClosedLoopPeriodFilterLowerRatio = 0.65;

    /// <summary>
    /// Верхнее отношение для фильтра единичных ложных/пропущенных crossing.
    /// </summary>
    public const double ClosedLoopPeriodFilterUpperRatio = 1.35;

    /// <summary>
    /// Максимальный коэффициент вариации периодов устойчивых колебаний.
    /// </summary>
    public const double MaximumClosedLoopPeriodCv = 0.15;

    /// <summary>
    /// Максимальный коэффициент вариации амплитуд устойчивых колебаний.
    /// </summary>
    public const double MaximumClosedLoopAmplitudeCv = 0.25;

    /// <summary>
    /// Если отношение поздней амплитуды к ранней ниже этого значения,
    /// колебания считаются затухающими.
    /// </summary>
    public const double MinimumClosedLoopAmplitudeTrendRatio = 0.75;

    /// <summary>
    /// Если отношение поздней амплитуды к ранней выше этого значения,
    /// колебания считаются растущими.
    /// </summary>
    public const double MaximumClosedLoopAmplitudeTrendRatio = 1.35;

    /// <summary>
    /// Период должен содержать минимум столько шагов архивного тренда,
    /// иначе найденные crossing слишком похожи на шум.
    /// </summary>
    public const double MinimumClosedLoopPeriodDtMultiple = 4.0;

    /// <summary>
    /// Робастный разброс SP P95-P05 не должен превышать эту долю
    /// peak-to-peak амплитуды detrended ошибки PV-SP.
    /// </summary>
    public const double MaximumClosedLoopSetpointVariationRatio = 0.10;

    /// <summary>
    /// Линейный дрейф SP за выбранное окно не должен превышать эту долю
    /// peak-to-peak амплитуды detrended ошибки PV-SP.
    /// </summary>
    public const double MaximumClosedLoopSetpointDriftRatio = 0.10;
}
