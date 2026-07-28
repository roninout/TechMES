namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Автоматически идентифицирует FOPDT-модель по ступенчатому тесту OUT/PV
/// и рассчитывает начальные SIMC PI-настройки в идеальной/ISA-форме.
/// </summary>
public static class SimcPidTuner
{
    private const double Epsilon = 1e-9;
    private const double ResponseAt28Percent = 0.283;
    private const double ResponseAt63Percent = 0.632;

    /// <summary>
    /// Рассчитывает FOPDT-модель по видимой области тренда.
    /// OUT обычно соответствует ManTune, PV - выбранной переменной процесса.
    /// Постоянная времени определяется двухточечным методом 28.3/63.2 процента.
    /// </summary>
    public static PidTuningResult Calculate(
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample> output)
    {
        if (pv is null || output is null || pv.Count == 0 || output.Count == 0)
            return Fail(
                PidTuneIssueCode.MissingTrendData,
                "Нет данных PV или OUT в выбранной области графика.");

        var pairs = AlignByTime(pv, output);
        if (pairs.Count < 8)
            return Fail(
                PidTuneIssueCode.InsufficientAlignedSamples,
                "Недостаточно общих точек PV и OUT для расчета.");

        var dt = EstimateStepSeconds(pairs);
        var stepIndex = FindLargestOutputStep(pairs, out var largestOutputStep);
        if (stepIndex < 2 || stepIndex >= pairs.Count - 2)
        {
            return Fail(
                PidTuneIssueCode.StepTooCloseToBoundary,
                "Ступень OUT расположена слишком близко к границе выбранной области.");
        }

        if (largestOutputStep <= Epsilon)
            return Fail(
                PidTuneIssueCode.NoOutStep,
                "В выбранном участке OUT не найдено ступенчатое изменение.");

        var outInitial = AverageBefore(pairs, stepIndex, item => item.Output);
        var outFinal = AverageTail(pairs, stepIndex, item => item.Output);
        var deltaOut = outFinal - outInitial;

        // Ступень должна сохраниться до конца выбранной области, иначе это не
        // классический ступенчатый тест и конечное усиление определить нельзя.
        if (Math.Abs(deltaOut) < Math.Max(Epsilon, largestOutputStep * 0.25))
        {
            return Fail(
                PidTuneIssueCode.OutStepNotSustained,
                "OUT не сохранил ступенчатое изменение до конца выбранной области.");
        }

        if (largestOutputStep < Math.Abs(deltaOut) * 0.5)
        {
            return Fail(
                PidTuneIssueCode.OutRampInsteadOfStep,
                "OUT изменялся постепенно: для идентификации нужна выраженная ступень.");
        }

        if (!IsSettledTail(
                pairs,
                stepIndex,
                item => item.Output,
                Math.Abs(deltaOut)))
        {
            return Fail(
                PidTuneIssueCode.OutNotSettled,
                "OUT не установился в конце выбранной области.");
        }

        var pvInitial = AverageBefore(pairs, stepIndex, item => item.Pv);
        var pvFinal = AverageTail(pairs, stepIndex, item => item.Pv);
        var deltaPv = pvFinal - pvInitial;

        if (Math.Abs(deltaPv) < Epsilon)
            return Fail(
                PidTuneIssueCode.PvNoResponse,
                "PV почти не изменилась после ступени OUT.");

        if (!IsSettledTail(
                pairs,
                stepIndex,
                item => item.Pv,
                Math.Abs(deltaPv)))
        {
            return Fail(
                PidTuneIssueCode.PvNotSettled,
                "PV не установилась в конце выбранной области. Расширьте видимый интервал.");
        }

        // Знак K сохраняется: отрицательное усиление описывает процесс
        // обратного действия и приводит к отрицательному Kp.
        var k = deltaPv / deltaOut;
        if (!IsFinite(k) || Math.Abs(k) < Epsilon)
            return Fail(
                PidTuneIssueCode.ProcessGainTooSmall,
                "Усиление процесса слишком мало для устойчивого расчета.");

        var stepTimeUtc = pairs[stepIndex].TimeUtc;
        var time28 = FindCrossingSeconds(
            pairs,
            stepIndex,
            pvInitial + ResponseAt28Percent * deltaPv,
            deltaPv,
            stepTimeUtc);
        var time63 = FindCrossingSeconds(
            pairs,
            stepIndex,
            pvInitial + ResponseAt63Percent * deltaPv,
            deltaPv,
            stepTimeUtc);

        if (time28 is null)
            return Fail(
                PidTuneIssueCode.PvDidNotReach28Percent,
                "PV не достигла 28.3% изменения в выбранной области.");

        if (time63 is null)
            return Fail(
                PidTuneIssueCode.PvDidNotReach63Percent,
                "PV не достигла 63.2% изменения в выбранной области.");

        if (time63 <= time28)
            return Fail(
                PidTuneIssueCode.InvalidResponseCrossings,
                "Точки 28.3% и 63.2% не образуют корректный отклик процесса.");

        // Для FOPDT: t28 = theta + 0.333*tau, t63 = theta + tau.
        // Отсюда tau = 1.5*(t63-t28), theta = t63-tau.
        var tau = 1.5 * (time63.Value - time28.Value);
        var theta = Math.Max(0, time63.Value - tau);

        if (!IsFinite(tau) || tau <= 0 || !IsFinite(theta))
            return Fail(
                PidTuneIssueCode.InvalidModelParameters,
                "Не удалось определить корректные Tau и Theta.");

        // Классический выбор SIMC - tauC=theta. При практически нулевой
        // задержке шаг дискретизации задает минимально разумную tauC.
        var tauC = Math.Max(theta, dt);
        var kp = (1.0 / k) * tau / (tauC + theta);
        var ti = Math.Min(tau, 4.0 * (tauC + theta));
        const double td = 0;

        if (!IsFinite(kp) || !IsFinite(ti) || ti <= 0)
            return Fail(
                PidTuneIssueCode.InvalidControllerParameters,
                "Расчет SIMC сформировал некорректные параметры регулятора.");

        return new PidTuningResult
        {
            IsSuccess = true,
            IssueCode = PidTuneIssueCode.None,
            K = Round(k, 4),
            T = Round(tau, 2),
            Theta = Round(theta, 2),
            TauC = Round(tauC, 2),
            Kp = Round(kp, 4),
            Ti = Round(ti, 2),
            Td = td,
            DtSeconds = Round(dt, 2),
            PointsUsed = pairs.Count,
            StepTimeUtc = stepTimeUtc
        };
    }

    private static PidTuningResult Fail(PidTuneIssueCode issueCode, string message)
    {
        return new PidTuningResult
        {
            IsSuccess = false,
            ErrorMessage = message,
            IssueCode = issueCode
        };
    }

    /// <summary>
    /// Сопоставляет PV и OUT по ближайшему времени. Слишком удаленные точки
    /// отбрасываются, чтобы разрывы одного тренда не искажали другой.
    /// </summary>
    private static List<TuningPair> AlignByTime(
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample> output)
    {
        var orderedPv = pv
            .Where(item => IsFinite(item.Value))
            .OrderBy(item => item.TimeUtc)
            .ToList();
        var orderedOutput = output
            .Where(item => IsFinite(item.Value))
            .OrderBy(item => item.TimeUtc)
            .ToList();
        var pairs = new List<TuningPair>();

        if (orderedPv.Count == 0 || orderedOutput.Count == 0)
            return pairs;

        var pvStep = EstimateSourceStepSeconds(orderedPv);
        var outputStep = EstimateSourceStepSeconds(orderedOutput);
        var maxDistanceSeconds = Math.Max(1, Math.Max(pvStep, outputStep) * 1.5);

        var outputIndex = 0;
        foreach (var pvPoint in orderedPv)
        {
            while (outputIndex + 1 < orderedOutput.Count
                   && AbsoluteSecondsBetween(
                       pvPoint.TimeUtc,
                       orderedOutput[outputIndex + 1].TimeUtc)
                   <= AbsoluteSecondsBetween(
                       pvPoint.TimeUtc,
                       orderedOutput[outputIndex].TimeUtc))
            {
                outputIndex++;
            }

            var outputPoint = orderedOutput[outputIndex];
            if (AbsoluteSecondsBetween(pvPoint.TimeUtc, outputPoint.TimeUtc)
                > maxDistanceSeconds)
            {
                continue;
            }

            pairs.Add(new TuningPair(
                pvPoint.TimeUtc,
                pvPoint.Value,
                outputPoint.Value));
        }

        return pairs;
    }

    private static int FindLargestOutputStep(
        IReadOnlyList<TuningPair> pairs,
        out double maxDelta)
    {
        var stepIndex = -1;
        maxDelta = 0;

        for (var i = 1; i < pairs.Count; i++)
        {
            var delta = Math.Abs(pairs[i].Output - pairs[i - 1].Output);
            if (delta > maxDelta)
            {
                maxDelta = delta;
                stepIndex = i;
            }
        }

        return stepIndex;
    }

    /// <summary>
    /// Находит время пересечения уровня с линейной интерполяцией между точками.
    /// Возвращаемое время отсчитывается от момента ступени OUT.
    /// </summary>
    private static double? FindCrossingSeconds(
        IReadOnlyList<TuningPair> pairs,
        int startIndex,
        double target,
        double direction,
        DateTime stepTimeUtc)
    {
        for (var i = Math.Max(1, startIndex); i < pairs.Count; i++)
        {
            var previous = pairs[i - 1];
            var current = pairs[i];
            var crossed = direction > 0
                ? previous.Pv <= target && current.Pv >= target
                : previous.Pv >= target && current.Pv <= target;

            if (!crossed)
                continue;

            var pvDelta = current.Pv - previous.Pv;
            var fraction = Math.Abs(pvDelta) <= Epsilon
                ? 0
                : Math.Clamp((target - previous.Pv) / pvDelta, 0, 1);
            var segmentSeconds = SecondsBetween(previous.TimeUtc, current.TimeUtc);
            var crossingTime = SecondsBetween(stepTimeUtc, previous.TimeUtc)
                               + fraction * segmentSeconds;

            return Math.Max(0, crossingTime);
        }

        return null;
    }

    private static double AverageBefore(
        IReadOnlyList<TuningPair> pairs,
        int stepIndex,
        Func<TuningPair, double> selector)
    {
        var take = Math.Min(5, stepIndex);
        return pairs
            .Skip(stepIndex - take)
            .Take(take)
            .Average(selector);
    }

    private static double AverageTail(
        IReadOnlyList<TuningPair> pairs,
        int stepIndex,
        Func<TuningPair, double> selector)
    {
        var skip = Math.Max(stepIndex + 1, pairs.Count - 5);
        return pairs.Skip(skip).Average(selector);
    }

    /// <summary>
    /// Проверяет, что последние точки образуют плато. Допуск в 10 процентов
    /// учитывает промышленный шум, но отсекает незавершенный переходный процесс.
    /// </summary>
    private static bool IsSettledTail(
        IReadOnlyList<TuningPair> pairs,
        int stepIndex,
        Func<TuningPair, double> selector,
        double totalChange)
    {
        var skip = Math.Max(stepIndex + 1, pairs.Count - 5);
        var tail = pairs.Skip(skip).Select(selector).ToList();
        if (tail.Count < 3)
            return false;

        var spread = tail.Max() - tail.Min();
        var tolerance = Math.Max(Epsilon * 10, totalChange * 0.1);
        return spread <= tolerance;
    }

    private static double EstimateStepSeconds(IReadOnlyList<TuningPair> pairs)
    {
        var deltas = pairs
            .Zip(pairs.Skip(1), (left, right) => SecondsBetween(left.TimeUtc, right.TimeUtc))
            .Where(delta => delta > 0)
            .OrderBy(delta => delta)
            .ToList();

        return MedianOrDefault(deltas, 1);
    }

    private static double EstimateSourceStepSeconds(
        IReadOnlyList<PidTuningSample> samples)
    {
        var deltas = samples
            .Zip(samples.Skip(1), (left, right) => SecondsBetween(left.TimeUtc, right.TimeUtc))
            .Where(delta => delta > 0)
            .OrderBy(delta => delta)
            .ToList();

        return MedianOrDefault(deltas, 1);
    }

    private static double MedianOrDefault(IReadOnlyList<double> ordered, double fallback)
    {
        return ordered.Count == 0
            ? fallback
            : ordered[ordered.Count / 2];
    }

    private static double SecondsBetween(DateTime leftUtc, DateTime rightUtc)
    {
        return (rightUtc - leftUtc).TotalSeconds;
    }

    private static double AbsoluteSecondsBetween(DateTime leftUtc, DateTime rightUtc)
    {
        return Math.Abs(SecondsBetween(leftUtc, rightUtc));
    }

    private static bool IsFinite(double value)
    {
        return double.IsFinite(value);
    }

    private static double Round(double value, int digits)
    {
        return Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }

    private sealed record TuningPair(DateTime TimeUtc, double Pv, double Output);
}
