namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Идентифицирует критические незатухающие колебания замкнутого контура.
///
/// В отличие от первой версии анализируется не один PV, а ошибка:
///
///     e(t) = PV(t) - SP(t)
///
/// Это защищает от ложного определения ultimate oscillation, когда сам SP
/// периодически изменяется и PV просто повторяет заданное воздействие.
///
/// Из трендов PV/SP определяется Tu.
/// Ku берется из текущего online Test Kp, поэтому Test Kp тренд не нужен.
///
/// Перед поиском периодов:
/// 1) PV и SP выравниваются по времени;
/// 2) проверяется стабильность SP;
/// 3) из ошибки PV-SP удаляется линейный дрейф;
/// 4) применяется слабое трехточечное сглаживание;
/// 5) периоды ищутся по восходящим zero-crossing с гистерезисом.
/// </summary>
public static class ClosedLoopOscillationIdentifier
{
    private const double Epsilon = 1e-12;

    public static PidProcessIdentificationResult Identify(
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample> sp,
        double? testKp)
    {
        if (!testKp.HasValue
            || !double.IsFinite(testKp.Value)
            || testKp.Value <= 0)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopTestKpInvalid,
                "Test Kp должен быть найден как положительное числовое online-значение.");
        }

        if (pv is null
            || sp is null
            || pv.Count == 0
            || sp.Count == 0)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.MissingTrendData,
                "Для ClosedLoop нужны одновременно trend-точки PV и SP.");
        }

        /*
         * AlignByTime исторически называет второй ряд Output.
         * В ClosedLoop поле Output содержит SP.
         */
        var pairs =
            PidTrendMath.AlignByTime(
                pv,
                sp);

        if (pairs.Count < 20)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                pairs.Count == 0
                    ? PidTuneIssueCode.MissingTrendData
                    : PidTuneIssueCode.InsufficientAlignedSamples,
                "Недостаточно синхронизированных точек PV/SP для анализа критических колебаний.",
                pairs.Count);
        }

        pairs =
            PidTrendMath.Downsample(
                pairs,
                4000);

        var dt =
            PidTrendMath.EstimateStepSeconds(
                pairs);

        var firstTime =
            pairs[0].TimeUtc;

        var time = pairs
            .Select(item =>
                PidTrendMath.SecondsBetween(
                    firstTime,
                    item.TimeUtc))
            .ToList();

        var error = pairs
            .Select(item =>
                item.Pv - item.Output)
            .ToList();

        var spValues = pairs
            .Select(item => item.Output)
            .ToList();

        /*
         * Сначала удаляем медленный линейный drift ошибки.
         * Это позволяет crossing detector не зависеть от небольшого смещения
         * рабочей точки во время теста.
         */
        if (!PidTrendMath.TryFitLine(
                time,
                error,
                out var errorIntercept,
                out var errorSlope))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.InvalidModelParameters,
                "Не удалось удалить линейный дрейф ошибки PV-SP.",
                pairs.Count,
                dt);
        }

        var residual =
            new List<double>(
                pairs.Count);

        for (var i = 0;
             i < pairs.Count;
             i++)
        {
            residual.Add(
                error[i]
                - (
                    errorIntercept
                    + errorSlope
                    * time[i]
                  ));
        }

        // radius=1 -> максимум трехточечное moving average.
        var smooth =
            PidTrendMath.Smooth(
                residual,
                radius: 1);

        /*
         * Робастная амплитуда:
         *
         *     Arobust = (P95 - P05) / 2
         *
         * Percentile вместо min/max снижает влияние единичных выбросов.
         */
        var robustAmplitude =
            PidTrendMath.RobustRange(
                smooth)
            / 2.0;

        if (!double.IsFinite(
                robustAmplitude)
            || robustAmplitude <= Epsilon)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopNoOscillation,
                "В выбранном участке ошибки PV-SP не найдены выраженные колебания.",
                pairs.Count,
                dt);
        }

        /*
         * SP должен быть практически постоянным.
         *
         * Сравниваем его робастный диапазон и линейный drift
         * не с абсолютной величиной SP, а с peak-to-peak амплитудой ошибки.
         * Так критерий работает одинаково для разных инженерных диапазонов.
         */
        var errorPeakToPeak =
            Math.Max(
                robustAmplitude * 2.0,
                Epsilon);

        var spVariationRatio =
            PidTrendMath.RobustRange(
                spValues)
            / errorPeakToPeak;

        if (!PidTrendMath.TryFitLine(
                time,
                spValues,
                out _,
                out var spSlope))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopSetpointUnstable,
                "Не удалось проверить стабильность SP в выбранном ClosedLoop-окне.",
                pairs.Count,
                dt);
        }

        var durationSeconds =
            Math.Max(
                0,
                time[^1] - time[0]);

        var spDriftRatio =
            Math.Abs(spSlope)
            * durationSeconds
            / errorPeakToPeak;

        if (!double.IsFinite(
                spVariationRatio)
            || !double.IsFinite(
                spDriftRatio)
            || spVariationRatio
            > PidTuneIdentificationRules
                .MaximumClosedLoopSetpointVariationRatio
            || spDriftRatio
            > PidTuneIdentificationRules
                .MaximumClosedLoopSetpointDriftRatio)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopSetpointUnstable,
                $"SP в выбранном окне недостаточно стабилен: "
                + $"variation ratio={spVariationRatio:0.###} "
                + $"(допустимо <= "
                + $"{PidTuneIdentificationRules.MaximumClosedLoopSetpointVariationRatio:0.###}), "
                + $"drift ratio={spDriftRatio:0.###} "
                + $"(допустимо <= "
                + $"{PidTuneIdentificationRules.MaximumClosedLoopSetpointDriftRatio:0.###}).",
                pairs.Count,
                dt);
        }

        var hysteresis =
            Math.Max(
                robustAmplitude
                * PidTuneIdentificationRules
                    .ClosedLoopCrossingHysteresisFraction,
                Epsilon * 10);

        var crossings =
            FindUpwardCrossings(
                time,
                smooth,
                hysteresis);

        if (crossings.Count
            < PidTuneIdentificationRules
                .MinimumClosedLoopCycles
              + 1)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopInsufficientCycles,
                $"Для ClosedLoop нужно не менее "
                + $"{PidTuneIdentificationRules.MinimumClosedLoopCycles} "
                + $"полных циклов ошибки PV-SP.",
                pairs.Count,
                dt);
        }

        var rawPeriods = crossings
            .Zip(
                crossings.Skip(1),
                (left, right) =>
                    right - left)
            .Where(value =>
                value > 0
                && double.IsFinite(value))
            .ToList();

        if (rawPeriods.Count
            < PidTuneIdentificationRules
                .MinimumClosedLoopCycles)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopInsufficientCycles,
                "Не удалось получить требуемое количество корректных периодов ошибки PV-SP.",
                pairs.Count,
                dt);
        }

        var initialMedianPeriod =
            PidTrendMath.Median(
                rawPeriods);

        /*
         * Удаляем только единичные ложные/пропущенные crossing.
         * После этого стабильность все равно проверяется через CV,
         * поэтому реальная нестационарность не маскируется.
         */
        var periods = rawPeriods
            .Where(value =>
                value
                >= initialMedianPeriod
                   * PidTuneIdentificationRules
                       .ClosedLoopPeriodFilterLowerRatio
                && value
                <= initialMedianPeriod
                   * PidTuneIdentificationRules
                       .ClosedLoopPeriodFilterUpperRatio)
            .ToList();

        if (periods.Count
            < PidTuneIdentificationRules
                .MinimumClosedLoopCycles)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopPeriodUnstable,
                "Период колебаний ошибки PV-SP нестабилен.",
                pairs.Count,
                dt);
        }

        /*
         * Tu = median(periods).
         * Медиана устойчивее среднего к одному плохому crossing.
         */
        var tu =
            PidTrendMath.Median(
                periods);

        if (tu
            < dt
              * PidTuneIdentificationRules
                  .MinimumClosedLoopPeriodDtMultiple)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopNoOscillation,
                "Найденные колебания слишком быстрые относительно шага тренда и похожи на шум.",
                pairs.Count,
                dt);
        }

        var periodCv =
            PidTrendMath.CoefficientOfVariation(
                periods);

        if (!double.IsFinite(
                periodCv)
            || periodCv
            > PidTuneIdentificationRules
                .MaximumClosedLoopPeriodCv)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopPeriodUnstable,
                $"Период колебаний нестабилен: "
                + $"CV={periodCv:0.###}, допустимо <= "
                + $"{PidTuneIdentificationRules.MaximumClosedLoopPeriodCv:0.###}.",
                pairs.Count,
                dt);
        }

        var cycleAmplitudes =
            CalculateCycleAmplitudes(
                time,
                smooth,
                crossings,
                initialMedianPeriod);

        if (cycleAmplitudes.Count
            < PidTuneIdentificationRules
                .MinimumClosedLoopCycles)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopInsufficientCycles,
                "Недостаточно полных циклов для проверки амплитуды.",
                pairs.Count,
                dt);
        }

        var meanAmplitude =
            cycleAmplitudes.Average();

        if (!double.IsFinite(
                meanAmplitude)
            || meanAmplitude <= Epsilon)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopNoOscillation,
                "Амплитуда колебаний ошибки PV-SP слишком мала.",
                pairs.Count,
                dt);
        }

        /*
         * Сравниваем первые и последние циклы:
         *
         * ratio = Alate / Aearly.
         *
         * ratio < 0.75 -> затухание;
         * ratio > 1.35 -> рост;
         * около 1      -> возможный ultimate point.
         */
        var comparisonCycles =
            Math.Min(
                2,
                cycleAmplitudes.Count / 2);

        var earlyAmplitude =
            cycleAmplitudes
                .Take(
                    comparisonCycles)
                .Average();

        var lateAmplitude =
            cycleAmplitudes
                .Skip(
                    cycleAmplitudes.Count
                    - comparisonCycles)
                .Average();

        var amplitudeTrendRatio =
            lateAmplitude
            / earlyAmplitude;

        if (amplitudeTrendRatio
            < PidTuneIdentificationRules
                .MinimumClosedLoopAmplitudeTrendRatio)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopOscillationsDamped,
                $"Колебания затухают: "
                + $"Alate/Aearly={amplitudeTrendRatio:0.###}. "
                + $"Текущий Test Kp еще ниже критической границы.",
                pairs.Count,
                dt);
        }

        if (amplitudeTrendRatio
            > PidTuneIdentificationRules
                .MaximumClosedLoopAmplitudeTrendRatio)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopOscillationsGrowing,
                $"Амплитуда колебаний растет: "
                + $"Alate/Aearly={amplitudeTrendRatio:0.###}. "
                + $"Текущий Test Kp уже выше критической границы.",
                pairs.Count,
                dt);
        }

        var amplitudeCv =
            PidTrendMath.CoefficientOfVariation(
                cycleAmplitudes);

        if (!double.IsFinite(
                amplitudeCv)
            || amplitudeCv
            > PidTuneIdentificationRules
                .MaximumClosedLoopAmplitudeCv)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopAmplitudeUnstable,
                $"Амплитуда колебаний нестабильна: "
                + $"CV={amplitudeCv:0.###}, допустимо <= "
                + $"{PidTuneIdentificationRules.MaximumClosedLoopAmplitudeCv:0.###}.",
                pairs.Count,
                dt);
        }

        return new PidProcessIdentificationResult
        {
            IsSuccess = true,
            ProcessModel =
                PidTuneProcessModel.ClosedLoop,
            IssueCode =
                PidTuneIssueCode.None,

            /*
             * В classic ultimate-gain test критическое усиление -
             * это фактический Kp, при котором подтверждены устойчивые
             * незатухающие колебания.
             */
            Ku =
                PidTrendMath.Round(
                    testKp.Value,
                    6),

            Tu =
                PidTrendMath.Round(
                    tu,
                    3),

            OscillationAmplitude =
                PidTrendMath.Round(
                    meanAmplitude,
                    6),

            PeriodCv =
                PidTrendMath.Round(
                    periodCv,
                    6),

            AmplitudeCv =
                PidTrendMath.Round(
                    amplitudeCv,
                    6),

            AmplitudeTrendRatio =
                PidTrendMath.Round(
                    amplitudeTrendRatio,
                    6),

            SetpointVariationRatio =
                PidTrendMath.Round(
                    spVariationRatio,
                    6),

            SetpointDriftRatio =
                PidTrendMath.Round(
                    spDriftRatio,
                    6),

            CyclesUsed =
                cycleAmplitudes.Count,

            DtSeconds =
                PidTrendMath.Round(
                    dt,
                    3),

            PointsUsed =
                pairs.Count
        };
    }

    /// <summary>
    /// Находит по одному восходящему zero-crossing на цикл.
    ///
    /// Детектор сначала "вооружается", когда сигнал уходит ниже -hysteresis,
    /// и только после этого принимает переход через 0 вверх.
    /// Это предотвращает многократные crossing из-за мелкого шума около нуля.
    /// </summary>
    private static List<double> FindUpwardCrossings(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        double hysteresis)
    {
        var result =
            new List<double>();

        var armed = false;

        double? lastNegativeTime = null;
        double? lastNegativeValue = null;

        for (var i = 0;
             i < values.Count;
             i++)
        {
            var value =
                values[i];

            if (value <= -hysteresis)
                armed = true;

            if (!armed)
                continue;

            if (value <= 0)
            {
                lastNegativeTime =
                    time[i];

                lastNegativeValue =
                    value;

                continue;
            }

            if (!lastNegativeTime.HasValue
                || !lastNegativeValue.HasValue)
            {
                continue;
            }

            var previousValue =
                lastNegativeValue.Value;

            var denominator =
                value - previousValue;

            var fraction =
                Math.Abs(denominator)
                <= Epsilon
                    ? 0
                    : Math.Clamp(
                        -previousValue
                        / denominator,
                        0,
                        1);

            var crossingTime =
                lastNegativeTime.Value
                + fraction
                * (
                    time[i]
                    - lastNegativeTime.Value
                  );

            result.Add(
                crossingTime);

            armed = false;
            lastNegativeTime = null;
            lastNegativeValue = null;
        }

        return result;
    }

    /// <summary>
    /// Для каждого полного периода определяет амплитуду как:
    ///
    ///     Acycle = (max - min) / 2.
    ///
    /// Периоды, явно не соответствующие исходной медиане crossing,
    /// не участвуют в оценке амплитуды.
    /// </summary>
    private static List<double> CalculateCycleAmplitudes(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        IReadOnlyList<double> crossings,
        double medianPeriod)
    {
        var result =
            new List<double>();

        for (var cycle = 0;
             cycle < crossings.Count - 1;
             cycle++)
        {
            var from =
                crossings[cycle];

            var to =
                crossings[cycle + 1];

            var period =
                to - from;

            if (period
                < medianPeriod
                  * PidTuneIdentificationRules
                      .ClosedLoopPeriodFilterLowerRatio
                || period
                > medianPeriod
                  * PidTuneIdentificationRules
                      .ClosedLoopPeriodFilterUpperRatio)
            {
                continue;
            }

            var minimum =
                double.PositiveInfinity;

            var maximum =
                double.NegativeInfinity;

            for (var i = 0;
                 i < time.Count;
                 i++)
            {
                if (time[i] < from
                    || time[i] > to)
                {
                    continue;
                }

                minimum =
                    Math.Min(
                        minimum,
                        values[i]);

                maximum =
                    Math.Max(
                        maximum,
                        values[i]);
            }

            if (!double.IsFinite(minimum)
                || !double.IsFinite(maximum))
            {
                continue;
            }

            var amplitude =
                (maximum - minimum)
                / 2.0;

            if (amplitude > Epsilon
                && double.IsFinite(
                    amplitude))
            {
                result.Add(
                    amplitude);
            }
        }

        return result;
    }
}
