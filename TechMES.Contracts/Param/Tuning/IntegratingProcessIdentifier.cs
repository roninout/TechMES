namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Идентифицирует интегрирующий процесс по изменению наклона PV после ступени OUT.
///
/// Используется кусочно-линейная модель:
///
///     PV(t) = a + b0*t + c*max(0, t - Theta)
///
/// где:
///     a       - уровень при t=0;
///     b0      - исходный дрейф PV до реакции;
///     b0 + c  - наклон PV после реакции;
///     c       - изменение наклона из-за ступени OUT;
///
///     ki = c / DeltaOUT.
///
/// Такой подход не требует искусственного "установившегося" значения PV,
/// которого у интегрирующего процесса по определению нет.
/// </summary>
public static class IntegratingProcessIdentifier
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
                PidTuneProcessModel.Integrating,
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
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.InsufficientAlignedSamples,
                "Недостаточно общих точек PV и OUT для идентификации интегрирующего процесса.",
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
                PidTuneProcessModel.Integrating,
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
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.StepTooCloseToBoundary,
                "Ступень OUT расположена слишком близко к границе выбранной области.",
                pairs.Count,
                dt);
        }

        if (!PidTrendMath.IsStepFastEnough(
                foundStep))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.OutRampInsteadOfStep,
                "OUT изменялся постепенно: для идентификации нужна выраженная ступень.",
                pairs.Count,
                dt);
        }

        if (!PidTrendMath.IsStepSustained(
                pairs,
                foundStep))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
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
                PidTuneProcessModel.Integrating,
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
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.NoOutStep,
                "Амплитуда ступени OUT слишком мала.",
                pairs.Count,
                dt);
        }

        var postDuration =
            PidTrendMath.SecondsBetween(
                foundStep.TimeUtc,
                pairs[^1].TimeUtc);

        if (postDuration
            <= Math.Max(
                5 * dt,
                1))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.StepTooCloseToBoundary,
                "После ступени OUT недостаточно времени для оценки изменения наклона PV.",
                pairs.Count,
                dt);
        }

        var fitPairs =
            PidTrendMath.Downsample(
                pairs,
                1600);

        var best =
            FindBestFit(
                fitPairs,
                foundStep.TimeUtc,
                dt,
                postDuration);

        if (best is null)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.InvalidModelParameters,
                "Не удалось подобрать кусочно-линейную модель интегрирующего процесса.",
                pairs.Count,
                dt);
        }

        var fit =
            best.Value;

        var ki =
            fit.SlopeChange
            / foundStep.Delta;

        if (!double.IsFinite(ki)
            || Math.Abs(ki) <= 1e-12)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.IntegratingSlopeNotDetected,
                "После ступени OUT не найдено устойчивое изменение наклона PV.",
                pairs.Count,
                dt);
        }

        var observed =
            new List<double>(
                pairs.Count);

        var predicted =
            new List<double>(
                pairs.Count);

        foreach (var point in pairs)
        {
            var timeSeconds =
                PidTrendMath.SecondsBetween(
                    foundStep.TimeUtc,
                    point.TimeUtc);

            var value =
                fit.Intercept
                + fit.BaseSlope
                * timeSeconds
                + fit.SlopeChange
                * Math.Max(
                    0,
                    timeSeconds - fit.Theta);

            observed.Add(
                point.Pv);

            predicted.Add(
                value);
        }

        var metrics =
            PidTrendMath.CalculateFit(
                observed,
                predicted);

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

        var pvNoise =
            PidTrendMath.StandardDeviation(
                prePv);

        /*
         * Эффект изменения наклона за весь post-window:
         *
         *     slopeContribution = |c| * Tpost.
         *
         * Он должен быть заметно больше исходного шума PV.
         */
        var slopeContribution =
            Math.Abs(
                fit.SlopeChange)
            * postDuration;

        if (slopeContribution
            < Math.Max(
                1e-9,
                pvNoise
                * PidTuneIdentificationRules
                    .MinimumIntegratingSlopeSignalToNoiseSigma))
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.IntegratingSlopeNotDetected,
                "Изменение наклона PV слишком мало относительно шума.",
                pairs.Count,
                dt);
        }

        if (!double.IsFinite(
                metrics.R2)
            || metrics.R2
            < PidTuneIdentificationRules
                .MinimumIntegratingR2)
        {
            return PidProcessIdentificationResult.Fail(
                PidTuneProcessModel.Integrating,
                PidTuneIssueCode.PoorModelFit,
                $"Интегрирующая модель плохо описывает выбранный участок: "
                + $"R²={metrics.R2:0.###}, требуется >= "
                + $"{PidTuneIdentificationRules.MinimumIntegratingR2:0.###}.",
                pairs.Count,
                dt);
        }

        var theta =
            fit.Theta
            < dt * 0.25
                ? 0
                : fit.Theta;

        /*
         * TauC - tuning parameter, а не физический параметр объекта.
         * Для автоматического стартового значения:
         *
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
                PidTuneProcessModel.Integrating,
            IssueCode =
                PidTuneIssueCode.None,

            Ki =
                PidTrendMath.Round(
                    ki,
                    8),

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

            BaseSlope =
                PidTrendMath.Round(
                    fit.BaseSlope,
                    8),

            SlopeChange =
                PidTrendMath.Round(
                    fit.SlopeChange,
                    8),

            Rmse =
                PidTrendMath.Round(
                    metrics.Rmse,
                    6),

            R2 =
                PidTrendMath.Round(
                    metrics.R2,
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
        double dt,
        double postDuration)
    {
        var maximumTheta =
            Math.Max(
                0,
                Math.Min(
                    postDuration * 0.40,
                    postDuration - 2 * dt));

        FitCandidate? best = null;

        const int coarseSteps = 100;

        for (var index = 0;
             index <= coarseSteps;
             index++)
        {
            var theta =
                maximumTheta <= 0
                    ? 0
                    : maximumTheta
                      * index
                      / coarseSteps;

            var candidate =
                EvaluateCandidate(
                    points,
                    stepTimeUtc,
                    theta);

            if (candidate is null)
                continue;

            if (best is null
                || candidate.Value.Sse
                < best.Value.Sse)
            {
                best = candidate;
            }
        }

        if (best is null)
            return null;

        var coarseThetaStep =
            maximumTheta <= 0
                ? dt
                : maximumTheta
                  / coarseSteps;

        var thetaFrom =
            Math.Max(
                0,
                best.Value.Theta
                - 3 * coarseThetaStep);

        var thetaTo =
            Math.Min(
                maximumTheta,
                best.Value.Theta
                + 3 * coarseThetaStep);

        const int refineSteps = 60;

        for (var index = 0;
             index <= refineSteps;
             index++)
        {
            var theta =
                thetaTo <= thetaFrom
                    ? thetaFrom
                    : thetaFrom
                      + (thetaTo - thetaFrom)
                      * index
                      / refineSteps;

            var candidate =
                EvaluateCandidate(
                    points,
                    stepTimeUtc,
                    theta);

            if (candidate is null)
                continue;

            if (candidate.Value.Sse
                < best.Value.Sse)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Для фиксированной Theta решает линейную МНК-задачу
    ///
    ///     PV = a + b0*t + c*max(0,t-Theta)
    ///
    /// по базисам [1, t, max(0,t-Theta)].
    ///
    /// Время нормализуется, чтобы матрица не теряла численную точность
    /// на длинных исторических интервалах.
    /// </summary>
    private static FitCandidate? EvaluateCandidate(
        IReadOnlyList<PidAlignedSample> points,
        DateTime stepTimeUtc,
        double theta)
    {
        var rawTimes = points
            .Select(point =>
                PidTrendMath.SecondsBetween(
                    stepTimeUtc,
                    point.TimeUtc))
            .ToList();

        var timeScale =
            Math.Max(
                1.0,
                rawTimes.Max(
                    Math.Abs));

        var matrix =
            new double[3, 3];

        var vector =
            new double[3];

        foreach (var (point, index)
                 in points.Select(
                     (value, index) =>
                         (value, index)))
        {
            var timeSeconds =
                rawTimes[index];

            var x0 = 1.0;

            var x1 =
                timeSeconds
                / timeScale;

            var x2 =
                Math.Max(
                    0,
                    timeSeconds - theta)
                / timeScale;

            var y =
                point.Pv;

            matrix[0, 0] += x0 * x0;
            matrix[0, 1] += x0 * x1;
            matrix[0, 2] += x0 * x2;

            matrix[1, 0] += x1 * x0;
            matrix[1, 1] += x1 * x1;
            matrix[1, 2] += x1 * x2;

            matrix[2, 0] += x2 * x0;
            matrix[2, 1] += x2 * x1;
            matrix[2, 2] += x2 * x2;

            vector[0] += x0 * y;
            vector[1] += x1 * y;
            vector[2] += x2 * y;
        }

        if (!PidTrendMath.TrySolve3x3(
                matrix,
                vector,
                out var coefficients))
        {
            return null;
        }

        var intercept =
            coefficients[0];

        var baseSlope =
            coefficients[1]
            / timeScale;

        var slopeChange =
            coefficients[2]
            / timeScale;

        var sse = 0.0;

        for (var i = 0;
             i < points.Count;
             i++)
        {
            var timeSeconds =
                rawTimes[i];

            var predicted =
                intercept
                + baseSlope
                * timeSeconds
                + slopeChange
                * Math.Max(
                    0,
                    timeSeconds - theta);

            var error =
                points[i].Pv
                - predicted;

            sse +=
                error * error;
        }

        if (!double.IsFinite(sse)
            || !double.IsFinite(baseSlope)
            || !double.IsFinite(slopeChange))
        {
            return null;
        }

        return new FitCandidate(
            theta,
            intercept,
            baseSlope,
            slopeChange,
            sse);
    }

    private readonly record struct FitCandidate(
        double Theta,
        double Intercept,
        double BaseSlope,
        double SlopeChange,
        double Sse);
}
