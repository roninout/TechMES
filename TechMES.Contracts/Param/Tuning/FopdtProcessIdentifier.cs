namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Идентифицирует FOPDT-модель
///
///     PV(t) = PV0 + A * (1 - exp(-(t - Theta) / Tau)),
///     A = K * DeltaOUT
///
/// по всему отклику после ступени OUT.
///
/// K, Tau и Theta совместно подбираются по минимуму квадратичной ошибки
/// всей видимой кривой. Старые точки 28,3 % / 63,2 % больше не используются
/// как способ вычисления Tau/Theta.
///
/// После fit отдельно проверяется, что выбранное окно действительно показывает
/// хотя бы приблизительно одну Tau после Theta. Это защищает от хорошо выглядящего,
/// но плохо идентифицируемого fit по слишком короткому начальному участку.
/// </summary>
public static class FopdtProcessIdentifier
{
    private const double Epsilon = 1e-12;

    public static PidProcessIdentificationResult Identify(
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample> output)
    {
        if (pv is null
            || output is null
            || pv.Count == 0
            || output.Count == 0)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.MissingTrendData,
                "Нет данных PV или OUT в выбранной области графика.");
        }

        var pairs =
            PidTrendMath.AlignByTime(
                pv,
                output);

        if (pairs.Count < 12)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.InsufficientAlignedSamples,
                "Недостаточно общих точек PV и OUT для идентификации FOPDT.",
                pairs.Count);
        }

        var dt =
            PidTrendMath.EstimateStepSeconds(
                pairs);

        var step =
            PidTrendMath.FindOutputStep(
                pairs);

        if (step is null)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.NoOutStep,
                "В выбранном участке OUT не найдено устойчивое ступенчатое изменение.",
                pairs.Count,
                dt);
        }

        var foundStep =
            step.Value;

        if (foundStep.Index < 4
            || foundStep.Index
            >= pairs.Count - 8)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.StepTooCloseToBoundary,
                "Ступень OUT расположена слишком близко к границе выбранной области.",
                pairs.Count,
                dt);
        }

        if (!PidTrendMath.IsStepFastEnough(
                foundStep))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.OutRampInsteadOfStep,
                "OUT изменялся постепенно: для FOPDT нужна выраженная ступень.",
                pairs.Count,
                dt);
        }

        if (!PidTrendMath.IsStepSustained(
                pairs,
                foundStep))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.OutStepNotSustained,
                "OUT не сохранил новый уровень до конца выбранной области.",
                pairs.Count,
                dt);
        }

        if (!PidTrendMath.IsOutputSettled(
                pairs,
                foundStep,
                out var outputTailRangeRatio))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.OutNotSettled,
                $"OUT после ступени не установился: "
                + $"robust tail range / |DeltaOUT| = {outputTailRangeRatio:0.###}, "
                + $"допустимо <= "
                + $"{PidTuneIdentificationRules.MaximumOutputTailRangeRatio:0.###}.",
                pairs.Count,
                dt);
        }

        if (Math.Abs(
                foundStep.Delta)
            <= Epsilon)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.NoOutStep,
                "Амплитуда ступени OUT слишком мала.",
                pairs.Count,
                dt);
        }

        var prePv = pairs
            .Skip(
                Math.Max(
                    0,
                    foundStep.Index - 8))
            .Take(
                Math.Min(
                    8,
                    foundStep.Index))
            .Select(item => item.Pv)
            .ToList();

        var pvBaseline =
            PidTrendMath.Median(
                prePv);

        var pvNoise =
            PidTrendMath.StandardDeviation(
                prePv);

        var post = pairs
            .Skip(foundStep.Index)
            .ToList();

        var durationSeconds =
            PidTrendMath.SecondsBetween(
                foundStep.TimeUtc,
                post[^1].TimeUtc);

        if (durationSeconds
            <= Math.Max(
                4 * dt,
                1))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.StepTooCloseToBoundary,
                "После ступени OUT недостаточно времени для идентификации отклика.",
                pairs.Count,
                dt);
        }

        var fitPoints =
            PidTrendMath.Downsample(
                post,
                1200);

        var best =
            FindBestFit(
                fitPoints,
                foundStep.TimeUtc,
                pvBaseline,
                dt,
                durationSeconds);

        if (best is null)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.InvalidModelParameters,
                "Не удалось подобрать устойчивые параметры FOPDT.",
                pairs.Count,
                dt);
        }

        var fit =
            best.Value;

        var k =
            fit.ResponseAmplitude
            / foundStep.Delta;

        if (!double.IsFinite(k)
            || Math.Abs(k) <= 1e-10)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.ProcessGainTooSmall,
                "Усиление процесса K слишком мало для устойчивого расчета.",
                pairs.Count,
                dt);
        }

        var responseAmplitude =
            Math.Abs(
                fit.ResponseAmplitude);

        var minimumUsefulResponse =
            Math.Max(
                1e-9,
                pvNoise
                * PidTuneIdentificationRules
                    .MinimumFopdtSignalToNoiseSigma);

        if (responseAmplitude
            < minimumUsefulResponse)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.PvNoResponse,
                "Реакция PV слишком мала относительно шума исходного участка.",
                pairs.Count,
                dt);
        }

        var observed =
            new List<double>(
                post.Count);

        var predicted =
            new List<double>(
                post.Count);

        foreach (var point in post)
        {
            var timeSeconds =
                Math.Max(
                    0,
                    PidTrendMath.SecondsBetween(
                        foundStep.TimeUtc,
                        point.TimeUtc));

            var modelFactor =
                CalculateModelFactor(
                    timeSeconds,
                    fit.Theta,
                    fit.Tau);

            observed.Add(
                point.Pv);

            predicted.Add(
                pvBaseline
                + fit.ResponseAmplitude
                * modelFactor);
        }

        var metrics =
            PidTrendMath.CalculateFit(
                observed,
                predicted);

        if (!double.IsFinite(
                metrics.R2)
            || metrics.R2
            < PidTuneIdentificationRules
                .MinimumFopdtR2)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.PoorModelFit,
                $"FOPDT плохо описывает выбранный отклик: "
                + $"R²={metrics.R2:0.###}, требуется >= "
                + $"{PidTuneIdentificationRules.MinimumFopdtR2:0.###}.",
                pairs.Count,
                dt);
        }

        /*
         * Проверка идентифицируемости:
         *
         * fitted response fraction =
         *     1 - exp(-(Tobserved - Theta) / Tau).
         *
         * При fraction < 1-exp(-1) мы не видели даже одну Tau после Theta.
         * В таком окне K и Tau сильно коррелируют:
         * разные пары K/Tau могут одинаково хорошо описывать раннюю часть кривой.
         */
        var observedResponseFraction =
            CalculateModelFactor(
                durationSeconds,
                fit.Theta,
                fit.Tau);

        if (!double.IsFinite(
                observedResponseFraction)
            || observedResponseFraction
            < PidTuneIdentificationRules
                .MinimumFopdtObservedResponseFraction)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Fopdt,
                PidTuneIssueCode.FopdtResponseWindowTooShort,
                $"Выбранное окно показывает только "
                + $"{observedResponseFraction * 100:0.#}% fitted FOPDT-отклика. "
                + $"Для надежной оценки K/Tau нужно не менее "
                + $"{PidTuneIdentificationRules.MinimumFopdtObservedResponseFraction * 100:0.#}% "
                + $"(приблизительно одна Tau после Theta).",
                pairs.Count,
                dt);
        }

        var theta =
            fit.Theta
            < dt * 0.25
                ? 0
                : fit.Theta;

        /*
         * TauC не является параметром объекта.
         * Это начальное значение tuning parameter для SIMC.
         *
         * Для робастного стартового значения используем:
         *     TauC = max(Theta, dt).
         */
        var tauC =
            Math.Max(
                theta,
                dt);

        return new PidProcessIdentificationResult
        {
            IsSuccess = true,
            ProcessModel =
                PidTuneProcessModel.Fopdt,
            IssueCode =
                PidTuneIssueCode.None,

            K =
                PidTrendMath.Round(
                    k,
                    6),

            Tau =
                PidTrendMath.Round(
                    fit.Tau,
                    3),

            Theta =
                PidTrendMath.Round(
                    theta,
                    3),

            TauC =
                PidTrendMath.Round(
                    tauC,
                    3),

            DeltaOut =
                PidTrendMath.Round(
                    foundStep.Delta,
                    6),

            PvBaseline =
                PidTrendMath.Round(
                    pvBaseline,
                    6),

            ResponseAmplitude =
                PidTrendMath.Round(
                    fit.ResponseAmplitude,
                    6),

            Rmse =
                PidTrendMath.Round(
                    metrics.Rmse,
                    6),

            R2 =
                PidTrendMath.Round(
                    metrics.R2,
                    6),

            ObservedResponseFraction =
                PidTrendMath.Round(
                    observedResponseFraction,
                    6),

            OutputTailRangeRatio =
                PidTrendMath.Round(
                    outputTailRangeRatio,
                    6),

            DtSeconds =
                PidTrendMath.Round(
                    dt,
                    3),

            PointsUsed =
                pairs.Count,

            StepTimeUtc =
                foundStep.TimeUtc
        };
    }

    private static FitCandidate? FindBestFit(
        IReadOnlyList<PidAlignedSample> points,
        DateTime stepTimeUtc,
        double pvBaseline,
        double dt,
        double durationSeconds)
    {
        var minimumTau =
            Math.Max(
                dt * 0.5,
                0.001);

        var maximumTau =
            Math.Max(
                minimumTau * 10,
                durationSeconds * 4.0);

        var maximumTheta =
            Math.Max(
                0,
                Math.Min(
                    durationSeconds * 0.60,
                    durationSeconds - dt));

        FitCandidate? best = null;

        const int thetaSteps = 40;
        const int tauSteps = 48;

        for (var thetaIndex = 0;
             thetaIndex <= thetaSteps;
             thetaIndex++)
        {
            var theta =
                maximumTheta <= 0
                    ? 0
                    : maximumTheta
                      * thetaIndex
                      / thetaSteps;

            for (var tauIndex = 0;
                 tauIndex <= tauSteps;
                 tauIndex++)
            {
                var tau =
                    LogInterpolate(
                        minimumTau,
                        maximumTau,
                        tauIndex,
                        tauSteps);

                var candidate =
                    EvaluateCandidate(
                        points,
                        stepTimeUtc,
                        pvBaseline,
                        theta,
                        tau);

                if (candidate is null)
                    continue;

                if (best is null
                    || candidate.Value.Sse
                    < best.Value.Sse)
                {
                    best = candidate;
                }
            }
        }

        if (best is null)
            return null;

        var coarseThetaStep =
            maximumTheta <= 0
                ? dt
                : maximumTheta
                  / thetaSteps;

        var thetaFrom =
            Math.Max(
                0,
                best.Value.Theta
                - 2 * coarseThetaStep);

        var thetaTo =
            Math.Min(
                maximumTheta,
                best.Value.Theta
                + 2 * coarseThetaStep);

        var tauFrom =
            Math.Max(
                minimumTau,
                best.Value.Tau
                * 0.55);

        var tauTo =
            Math.Min(
                maximumTau,
                best.Value.Tau
                * 1.80);

        const int refineSteps = 30;

        for (var thetaIndex = 0;
             thetaIndex <= refineSteps;
             thetaIndex++)
        {
            var theta =
                thetaTo <= thetaFrom
                    ? thetaFrom
                    : thetaFrom
                      + (thetaTo - thetaFrom)
                      * thetaIndex
                      / refineSteps;

            for (var tauIndex = 0;
                 tauIndex <= refineSteps;
                 tauIndex++)
            {
                var tau =
                    LogInterpolate(
                        tauFrom,
                        tauTo,
                        tauIndex,
                        refineSteps);

                var candidate =
                    EvaluateCandidate(
                        points,
                        stepTimeUtc,
                        pvBaseline,
                        theta,
                        tau);

                if (candidate is null)
                    continue;

                if (candidate.Value.Sse
                    < best.Value.Sse)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Для фиксированных Theta/Tau оптимальная полная амплитуда A
    /// вычисляется аналитически методом наименьших квадратов:
    ///
    ///     f_i = 1 - exp(-(t_i-Theta)/Tau)
    ///     A   = sum(f_i * (PV_i-PV0)) / sum(f_i²)
    ///
    /// затем считается SSE всей кривой.
    /// </summary>
    private static FitCandidate? EvaluateCandidate(
        IReadOnlyList<PidAlignedSample> points,
        DateTime stepTimeUtc,
        double pvBaseline,
        double theta,
        double tau)
    {
        if (tau <= 0
            || !double.IsFinite(tau))
        {
            return null;
        }

        var numerator = 0.0;
        var denominator = 0.0;

        foreach (var point in points)
        {
            var timeSeconds =
                Math.Max(
                    0,
                    PidTrendMath.SecondsBetween(
                        stepTimeUtc,
                        point.TimeUtc));

            var modelFactor =
                CalculateModelFactor(
                    timeSeconds,
                    theta,
                    tau);

            numerator +=
                modelFactor
                * (point.Pv - pvBaseline);

            denominator +=
                modelFactor
                * modelFactor;
        }

        if (denominator <= Epsilon)
            return null;

        var amplitude =
            numerator
            / denominator;

        if (!double.IsFinite(
                amplitude))
        {
            return null;
        }

        var sse = 0.0;

        foreach (var point in points)
        {
            var timeSeconds =
                Math.Max(
                    0,
                    PidTrendMath.SecondsBetween(
                        stepTimeUtc,
                        point.TimeUtc));

            var modelFactor =
                CalculateModelFactor(
                    timeSeconds,
                    theta,
                    tau);

            var predicted =
                pvBaseline
                + amplitude
                * modelFactor;

            var error =
                point.Pv
                - predicted;

            sse +=
                error * error;
        }

        return double.IsFinite(sse)
            ? new FitCandidate(
                theta,
                tau,
                amplitude,
                sse)
            : null;
    }

    private static double CalculateModelFactor(
        double timeSeconds,
        double theta,
        double tau)
    {
        if (timeSeconds <= theta
            || tau <= 0)
        {
            return 0;
        }

        return 1.0
               - Math.Exp(
                   -(timeSeconds - theta)
                   / tau);
    }

    private static double LogInterpolate(
        double minimum,
        double maximum,
        int index,
        int steps)
    {
        if (steps <= 0
            || maximum <= minimum)
        {
            return minimum;
        }

        var fraction =
            (double)index
            / steps;

        return minimum
               * Math.Pow(
                   maximum / minimum,
                   fraction);
    }

    private readonly record struct FitCandidate(
        double Theta,
        double Tau,
        double ResponseAmplitude,
        double Sse);
}
