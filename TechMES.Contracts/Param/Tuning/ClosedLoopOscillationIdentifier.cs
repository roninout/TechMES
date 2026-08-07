namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Идентифицирует критические незатухающие колебания замкнутого контура.
///
/// Из тренда PV определяется Tu.
/// Ku берется из текущего online Test Kp, потому сам Test Kp тренд не нужен.
///
/// Перед анализом из PV удаляется линейный дрейф, затем применяется слабое
/// сглаживание. Период определяется по восходящим переходам через ноль
/// с гистерезисом, что существенно устойчивее простого поиска локальных пиков.
/// </summary>
public static class ClosedLoopOscillationIdentifier
{
    private const double Epsilon = 1e-12;

    public static PidProcessIdentificationResult Identify(
        IReadOnlyList<PidTuningSample> pv,
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

        var samples = PidTrendMath.NormalizeSamples(pv);

        if (samples.Count < 20)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                samples.Count == 0
                    ? PidTuneIssueCode.MissingTrendData
                    : PidTuneIssueCode.InsufficientAlignedSamples,
                "Недостаточно точек PV для анализа критических колебаний.",
                samples.Count);
        }

        samples = PidTrendMath.Downsample(
            samples,
            4000);

        var dt = PidTrendMath.EstimateStepSeconds(samples);
        var firstTime = samples[0].TimeUtc;

        var time = samples
            .Select(item =>
                PidTrendMath.SecondsBetween(
                    firstTime,
                    item.TimeUtc))
            .ToList();

        var values = samples
            .Select(item => item.Value)
            .ToList();

        if (!PidTrendMath.TryFitLine(
                time,
                values,
                out var intercept,
                out var slope))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.InvalidModelParameters,
                "Не удалось удалить линейный дрейф PV.",
                samples.Count,
                dt);
        }

        var residual = new List<double>(samples.Count);

        for (var i = 0; i < samples.Count; i++)
        {
            residual.Add(
                values[i]
                - (intercept + slope * time[i]));
        }

        // Трехточечное сглаживание подавляет одиночный шум,
        // но практически не меняет период технологических колебаний.
        var smooth = PidTrendMath.Smooth(
            residual,
            radius: 1);

        var robustAmplitude =
            (PidTrendMath.Percentile(smooth, 0.95)
             - PidTrendMath.Percentile(smooth, 0.05))
            / 2.0;

        if (!double.IsFinite(robustAmplitude)
            || robustAmplitude <= Epsilon)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopNoOscillation,
                "В выбранном участке PV не найдены выраженные колебания.",
                samples.Count,
                dt);
        }

        var hysteresis = Math.Max(
            robustAmplitude * 0.15,
            Epsilon * 10);

        var crossings = FindUpwardCrossings(
            time,
            smooth,
            hysteresis);

        if (crossings.Count < 5)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopInsufficientCycles,
                "Для ClosedLoop нужно не менее четырех полных циклов PV.",
                samples.Count,
                dt);
        }

        var rawPeriods = crossings
            .Zip(
                crossings.Skip(1),
                (left, right) => right - left)
            .Where(value => value > 0 && double.IsFinite(value))
            .ToList();

        if (rawPeriods.Count < 4)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopInsufficientCycles,
                "Не удалось получить четыре корректных периода PV.",
                samples.Count,
                dt);
        }

        var initialMedianPeriod =
            PidTrendMath.Median(rawPeriods);

        // Удаляем редкие лишние/пропущенные crossing, но не маскируем
        // реально нестабильные колебания.
        var periods = rawPeriods
            .Where(value =>
                value >= initialMedianPeriod * 0.65
                && value <= initialMedianPeriod * 1.35)
            .ToList();

        if (periods.Count < 4)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopPeriodUnstable,
                "Период колебаний PV нестабилен.",
                samples.Count,
                dt);
        }

        var tu = PidTrendMath.Median(periods);

        if (tu < dt * 4)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopNoOscillation,
                "Найденные колебания слишком быстрые относительно шага тренда и похожи на шум.",
                samples.Count,
                dt);
        }

        var periodCv =
            PidTrendMath.CoefficientOfVariation(periods);

        if (!double.IsFinite(periodCv)
            || periodCv > 0.15)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopPeriodUnstable,
                $"Период колебаний нестабилен: CV={periodCv:0.###}.",
                samples.Count,
                dt);
        }

        var cycleAmplitudes = CalculateCycleAmplitudes(
            time,
            smooth,
            crossings,
            initialMedianPeriod);

        if (cycleAmplitudes.Count < 4)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopInsufficientCycles,
                "Недостаточно полных циклов для проверки амплитуды.",
                samples.Count,
                dt);
        }

        var meanAmplitude = cycleAmplitudes.Average();

        if (!double.IsFinite(meanAmplitude)
            || meanAmplitude <= Epsilon)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopNoOscillation,
                "Амплитуда колебаний PV слишком мала.",
                samples.Count,
                dt);
        }

        var comparisonCycles = Math.Min(
            2,
            cycleAmplitudes.Count / 2);

        var earlyAmplitude = cycleAmplitudes
            .Take(comparisonCycles)
            .Average();

        var lateAmplitude = cycleAmplitudes
            .Skip(cycleAmplitudes.Count - comparisonCycles)
            .Average();

        var amplitudeTrendRatio =
            lateAmplitude / earlyAmplitude;

        // Сначала различаем явно затухающие и явно растущие колебания.
        // Только после этого проверяем общий разброс амплитуд.
        if (amplitudeTrendRatio < 0.75)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopOscillationsDamped,
                $"Колебания затухают: отношение поздней амплитуды к ранней = {amplitudeTrendRatio:0.###}.",
                samples.Count,
                dt);
        }

        if (amplitudeTrendRatio > 1.35)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopOscillationsGrowing,
                $"Амплитуда колебаний растет: отношение поздней амплитуды к ранней = {amplitudeTrendRatio:0.###}.",
                samples.Count,
                dt);
        }

        var amplitudeCv =
            PidTrendMath.CoefficientOfVariation(
                cycleAmplitudes);

        if (!double.IsFinite(amplitudeCv)
            || amplitudeCv > 0.25)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.ClosedLoop,
                PidTuneIssueCode.ClosedLoopAmplitudeUnstable,
                $"Амплитуда колебаний нестабильна: CV={amplitudeCv:0.###}.",
                samples.Count,
                dt);
        }

        return new PidProcessIdentificationResult
        {
            IsSuccess = true,
            ProcessModel = PidTuneProcessModel.ClosedLoop,
            IssueCode = PidTuneIssueCode.None,
            Ku = PidTrendMath.Round(testKp.Value, 6),
            Tu = PidTrendMath.Round(tu, 3),
            OscillationAmplitude =
                PidTrendMath.Round(meanAmplitude, 6),
            PeriodCv =
                PidTrendMath.Round(periodCv, 6),
            AmplitudeCv =
                PidTrendMath.Round(amplitudeCv, 6),
            AmplitudeTrendRatio =
                PidTrendMath.Round(amplitudeTrendRatio, 6),
            DtSeconds =
                PidTrendMath.Round(dt, 3),
            PointsUsed = samples.Count
        };
    }

    /// <summary>
    /// Находит по одному восходящему crossing на цикл.
    /// Новый crossing разрешается только после ухода сигнала ниже -hysteresis.
    /// </summary>
    private static List<double> FindUpwardCrossings(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        double hysteresis)
    {
        var result = new List<double>();
        var armed = false;
        double? lastNegativeTime = null;
        double? lastNegativeValue = null;

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];

            if (value <= -hysteresis)
                armed = true;

            if (!armed)
                continue;

            if (value <= 0)
            {
                lastNegativeTime = time[i];
                lastNegativeValue = value;
                continue;
            }

            if (!lastNegativeTime.HasValue
                || !lastNegativeValue.HasValue)
            {
                continue;
            }

            var previousValue = lastNegativeValue.Value;
            var denominator = value - previousValue;

            var fraction = Math.Abs(denominator) <= Epsilon
                ? 0
                : Math.Clamp(
                    -previousValue / denominator,
                    0,
                    1);

            var crossingTime =
                lastNegativeTime.Value
                + fraction
                * (time[i] - lastNegativeTime.Value);

            result.Add(crossingTime);

            armed = false;
            lastNegativeTime = null;
            lastNegativeValue = null;
        }

        return result;
    }

    private static List<double> CalculateCycleAmplitudes(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        IReadOnlyList<double> crossings,
        double medianPeriod)
    {
        var result = new List<double>();

        for (var cycle = 0;
             cycle < crossings.Count - 1;
             cycle++)
        {
            var from = crossings[cycle];
            var to = crossings[cycle + 1];
            var period = to - from;

            if (period < medianPeriod * 0.65
                || period > medianPeriod * 1.35)
            {
                continue;
            }

            var minimum = double.PositiveInfinity;
            var maximum = double.NegativeInfinity;

            for (var i = 0; i < time.Count; i++)
            {
                if (time[i] < from || time[i] > to)
                    continue;

                minimum = Math.Min(minimum, values[i]);
                maximum = Math.Max(maximum, values[i]);
            }

            if (!double.IsFinite(minimum)
                || !double.IsFinite(maximum))
            {
                continue;
            }

            var amplitude = (maximum - minimum) / 2.0;

            if (amplitude > Epsilon
                && double.IsFinite(amplitude))
            {
                result.Add(amplitude);
            }
        }

        return result;
    }
}
