namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// SIMC PID tuner для ступенчатого теста процесса.
/// На вход получает выбранные пользователем участки PV и OUT, на выходе дает FOPDT-модель и PID.
/// </summary>
public static class SimcPidTuner
{
    private const double Epsilon = 1e-5;

    /// <summary>
    /// Рассчитывает FOPDT-модель и SIMC PID по видимой области тренда.
    /// OUT обычно соответствует ManTune, PV - выбранной пользователем переменной процесса.
    /// </summary>
    public static PidTuningResult Calculate(
        IReadOnlyList<PidTuningSample> pv,
        IReadOnlyList<PidTuningSample> output)
    {
        if (pv.Count == 0 || output.Count == 0)
            return Fail("Нет данных PV или OUT в выбранной области графика.");

        var pairs = AlignByTime(pv, output);
        if (pairs.Count < 5)
            return Fail("Недостаточно общих точек PV и OUT для расчета.");

        var dt = EstimateStepSeconds(pairs);
        var stepIndex = FindLargestOutputStep(pairs);
        var outInitial = AverageBefore(pairs, stepIndex, item => item.Output);
        var outFinal = AverageTail(pairs, item => item.Output);
        var deltaOut = outFinal - outInitial;

        if (Math.Abs(deltaOut) < Epsilon)
            return Fail("В выбранном участке OUT не найдено ступенчатое изменение.");

        var pvInitial = AverageBefore(pairs, stepIndex, item => item.Pv);
        var pvFinal = AverageTail(pairs, item => item.Pv);
        var deltaPv = pvFinal - pvInitial;

        if (Math.Abs(deltaPv) < Epsilon)
            return Fail("PV почти не изменилась после ступени OUT.");

        var k = deltaPv / deltaOut;
        if (Math.Abs(k) < Epsilon)
            return Fail("Усиление процесса слишком мало для устойчивого расчета.");

        var deadTimeIndex = FindCrossing(
            pairs,
            stepIndex,
            pvInitial + 0.02 * deltaPv,
            deltaPv);

        if (deadTimeIndex < 0)
            return Fail("Не удалось найти начало реакции PV.");

        var time63Index = FindCrossing(
            pairs,
            deadTimeIndex,
            pvInitial + 0.632 * deltaPv,
            deltaPv);

        if (time63Index < 0)
            return Fail("PV не достигла 63.2% изменения в выбранной области.");

        var theta = SecondsBetween(pairs[stepIndex].TimeUtc, pairs[deadTimeIndex].TimeUtc);
        var t = SecondsBetween(pairs[deadTimeIndex].TimeUtc, pairs[time63Index].TimeUtc);

        if (theta <= 0)
            theta = dt;

        if (t <= 0)
            t = dt;

        var lambda = theta;
        var kp = (1.0 / k) * (t / (theta + lambda));
        var ti = Math.Min(t, 4.0 * (theta + lambda));
        var td = theta / 2.0;

        return new PidTuningResult
        {
            IsSuccess = true,
            K = Round(k, 4),
            T = Round(t, 2),
            Theta = Round(theta, 2),
            Kp = Round(kp, 4),
            Ti = Round(ti, 2),
            Td = Round(td, 2),
            DtSeconds = Round(dt, 2),
            PointsUsed = pairs.Count,
            StepTimeUtc = pairs[stepIndex].TimeUtc
        };
    }

    private static PidTuningResult Fail(string message)
    {
        return new PidTuningResult
        {
            IsSuccess = false,
            ErrorMessage = message
        };
    }

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

        var outputIndex = 0;
        for (var pvIndex = 0; pvIndex < orderedPv.Count; pvIndex++)
        {
            var pvPoint = orderedPv[pvIndex];
            while (outputIndex + 1 < orderedOutput.Count
                   && Math.Abs(SecondsBetween(pvPoint.TimeUtc, orderedOutput[outputIndex + 1].TimeUtc))
                   <= Math.Abs(SecondsBetween(pvPoint.TimeUtc, orderedOutput[outputIndex].TimeUtc)))
            {
                outputIndex++;
            }

            pairs.Add(new TuningPair(
                pvPoint.TimeUtc,
                pvPoint.Value,
                orderedOutput[outputIndex].Value));
        }

        return pairs;
    }

    private static int FindLargestOutputStep(IReadOnlyList<TuningPair> pairs)
    {
        var stepIndex = 1;
        var maxDelta = 0d;

        for (var i = 1; i < pairs.Count - 1; i++)
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

    private static int FindCrossing(
        IReadOnlyList<TuningPair> pairs,
        int startIndex,
        double target,
        double direction)
    {
        for (var i = Math.Max(0, startIndex); i < pairs.Count; i++)
        {
            var crossed = direction > 0
                ? pairs[i].Pv >= target
                : pairs[i].Pv <= target;

            if (crossed)
                return i;
        }

        return -1;
    }

    private static double AverageBefore(
        IReadOnlyList<TuningPair> pairs,
        int stepIndex,
        Func<TuningPair, double> selector)
    {
        var take = Math.Min(5, Math.Max(1, stepIndex));
        var skip = Math.Max(0, stepIndex - take);
        return pairs.Skip(skip).Take(take).Average(selector);
    }

    private static double AverageTail(
        IReadOnlyList<TuningPair> pairs,
        Func<TuningPair, double> selector)
    {
        return pairs.Skip(Math.Max(0, pairs.Count - 5)).Average(selector);
    }

    private static double EstimateStepSeconds(IReadOnlyList<TuningPair> pairs)
    {
        var deltas = pairs
            .Zip(pairs.Skip(1), (left, right) => SecondsBetween(left.TimeUtc, right.TimeUtc))
            .Where(delta => delta > 0)
            .OrderBy(delta => delta)
            .ToList();

        if (deltas.Count == 0)
            return 1;

        return deltas[deltas.Count / 2];
    }

    private static double SecondsBetween(DateTime leftUtc, DateTime rightUtc)
    {
        return (rightUtc - leftUtc).TotalSeconds;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double Round(double value, int digits)
    {
        return Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }

    private sealed record TuningPair(DateTime TimeUtc, double Pv, double Output);
}
