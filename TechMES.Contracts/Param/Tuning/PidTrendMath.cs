namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Одна синхронизированная пара PV/OUT.
/// </summary>
internal readonly record struct PidAlignedSample(
    DateTime TimeUtc,
    double Pv,
    double Output);

/// <summary>
/// Найденная ступень OUT.
/// </summary>
internal readonly record struct PidOutputStep(
    int Index,
    DateTime TimeUtc,
    double Before,
    double After,
    double Delta,
    double AdjacentDelta);

/// <summary>
/// Метрики качества аппроксимации.
/// </summary>
internal readonly record struct PidFitMetrics(
    double Rmse,
    double R2);

/// <summary>
/// Общая математика для идентификаторов PID Tune.
/// Здесь нет зависимостей от WEB, Runtime, CtApi и PostgreSQL.
/// </summary>
internal static class PidTrendMath
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// Нормализует точки: удаляет NaN/Infinity, приводит время к UTC,
    /// объединяет дубликаты timestamp и сортирует по времени.
    /// </summary>
    public static List<PidTuningSample> NormalizeSamples(
        IReadOnlyList<PidTuningSample>? samples)
    {
        if (samples is null || samples.Count == 0)
            return [];

        return samples
            .Where(item => double.IsFinite(item.Value))
            .GroupBy(item => NormalizeUtc(item.TimeUtc))
            .Select(group => new PidTuningSample(
                group.Key,
                group.Average(item => item.Value)))
            .OrderBy(item => item.TimeUtc)
            .ToList();
    }

    /// <summary>
    /// Сопоставляет PV и OUT по ближайшему времени.
    /// Слишком удалённые точки отбрасываются.
    /// </summary>
    public static List<PidAlignedSample> AlignByTime(
        IReadOnlyList<PidTuningSample>? pv,
        IReadOnlyList<PidTuningSample>? output)
    {
        var orderedPv = NormalizeSamples(pv);
        var orderedOutput = NormalizeSamples(output);
        var result = new List<PidAlignedSample>();

        if (orderedPv.Count == 0 || orderedOutput.Count == 0)
            return result;

        var pvStep = EstimateStepSeconds(orderedPv);
        var outputStep = EstimateStepSeconds(orderedOutput);
        var maxDistanceSeconds = Math.Max(
            1.0,
            Math.Max(pvStep, outputStep) * 1.5);

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
            if (AbsoluteSecondsBetween(
                    pvPoint.TimeUtc,
                    outputPoint.TimeUtc) > maxDistanceSeconds)
            {
                continue;
            }

            result.Add(new PidAlignedSample(
                pvPoint.TimeUtc,
                pvPoint.Value,
                outputPoint.Value));
        }

        return result;
    }

    /// <summary>
    /// Ищет резкую и одновременно удерживаемую ступень OUT.
    /// Одиночный выброс не проходит, потому что сравниваются также медианы
    /// нескольких точек до и после кандидата.
    /// </summary>
    public static PidOutputStep? FindOutputStep(
        IReadOnlyList<PidAlignedSample> samples)
    {
        if (samples.Count < 8)
            return null;

        var window = Math.Clamp(samples.Count / 20, 3, 8);

        var bestScore = 0.0;
        PidOutputStep? best = null;

        for (var i = window; i < samples.Count - window; i++)
        {
            var before = Median(
                samples
                    .Skip(i - window)
                    .Take(window)
                    .Select(item => item.Output));

            var after = Median(
                samples
                    .Skip(i)
                    .Take(window)
                    .Select(item => item.Output));

            var adjacentDelta =
                samples[i].Output - samples[i - 1].Output;

            var sustainedDelta = after - before;

            // Для настоящей ступени большим должен быть и мгновенный скачок,
            // и устойчивое различие уровней до/после.
            var score = Math.Min(
                Math.Abs(adjacentDelta),
                Math.Abs(sustainedDelta));

            if (score <= bestScore)
                continue;

            bestScore = score;
            best = new PidOutputStep(
                i,
                samples[i].TimeUtc,
                before,
                after,
                sustainedDelta,
                adjacentDelta);
        }

        return bestScore > Epsilon ? best : null;
    }

    /// <summary>
    /// Проверяет, что OUT изменился именно ступенью, а не длинной рампой.
    /// </summary>
    public static bool IsStepFastEnough(PidOutputStep step)
    {
        return Math.Abs(step.AdjacentDelta)
               >= Math.Abs(step.Delta) * 0.5;
    }

    /// <summary>
    /// Проверяет, что новый уровень OUT удерживается до конца выбранной области.
    /// </summary>
    public static bool IsStepSustained(
        IReadOnlyList<PidAlignedSample> samples,
        PidOutputStep step)
    {
        var tailCount = Math.Min(
            8,
            Math.Max(0, samples.Count - step.Index));

        if (tailCount < 3)
            return false;

        var tail = Median(
            samples
                .Skip(samples.Count - tailCount)
                .Select(item => item.Output));

        var tolerance = Math.Max(
            1e-9,
            Math.Abs(step.Delta) * 0.20);

        return Math.Abs(tail - step.After) <= tolerance;
    }

    public static double EstimateStepSeconds(
        IReadOnlyList<PidTuningSample> samples)
    {
        if (samples.Count < 2)
            return 1;

        var deltas = samples
            .Zip(
                samples.Skip(1),
                (left, right) =>
                    SecondsBetween(left.TimeUtc, right.TimeUtc))
            .Where(value => value > 0 && double.IsFinite(value))
            .OrderBy(value => value)
            .ToList();

        return deltas.Count == 0
            ? 1
            : Median(deltas);
    }

    public static double EstimateStepSeconds(
        IReadOnlyList<PidAlignedSample> samples)
    {
        if (samples.Count < 2)
            return 1;

        var deltas = samples
            .Zip(
                samples.Skip(1),
                (left, right) =>
                    SecondsBetween(left.TimeUtc, right.TimeUtc))
            .Where(value => value > 0 && double.IsFinite(value))
            .OrderBy(value => value)
            .ToList();

        return deltas.Count == 0
            ? 1
            : Median(deltas);
    }

    /// <summary>
    /// Равномерно уменьшает количество точек для перебора модели,
    /// сохраняя начало и конец диапазона.
    /// </summary>
    public static List<T> Downsample<T>(
        IReadOnlyList<T> source,
        int maxPoints)
    {
        if (source.Count <= maxPoints || maxPoints < 2)
            return source.ToList();

        var result = new List<T>(maxPoints);

        for (var i = 0; i < maxPoints; i++)
        {
            var position =
                i * (source.Count - 1d) / (maxPoints - 1d);

            var index = (int)Math.Round(
                position,
                MidpointRounding.AwayFromZero);

            result.Add(source[index]);
        }

        return result;
    }

    public static PidFitMetrics CalculateFit(
        IReadOnlyList<double> observed,
        IReadOnlyList<double> predicted)
    {
        if (observed.Count == 0 || observed.Count != predicted.Count)
            return new PidFitMetrics(double.PositiveInfinity, 0);

        var mean = observed.Average();
        var sse = 0.0;
        var sst = 0.0;

        for (var i = 0; i < observed.Count; i++)
        {
            var error = observed[i] - predicted[i];
            sse += error * error;

            var centered = observed[i] - mean;
            sst += centered * centered;
        }

        var rmse = Math.Sqrt(sse / observed.Count);
        var r2 = sst <= Epsilon
            ? 0
            : 1.0 - sse / sst;

        return new PidFitMetrics(rmse, r2);
    }

    /// <summary>
    /// Решает систему 3x3 методом Гаусса с частичным выбором главного элемента.
    /// </summary>
    public static bool TrySolve3x3(
        double[,] matrix,
        double[] vector,
        out double[] solution)
    {
        solution = new double[3];
        var augmented = new double[3, 4];

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
                augmented[row, column] = matrix[row, column];

            augmented[row, 3] = vector[row];
        }

        for (var column = 0; column < 3; column++)
        {
            var pivotRow = column;
            var pivotAbs = Math.Abs(augmented[pivotRow, column]);

            for (var row = column + 1; row < 3; row++)
            {
                var candidate = Math.Abs(augmented[row, column]);
                if (candidate <= pivotAbs)
                    continue;

                pivotAbs = candidate;
                pivotRow = row;
            }

            if (pivotAbs <= Epsilon)
                return false;

            if (pivotRow != column)
            {
                for (var currentColumn = column;
                     currentColumn < 4;
                     currentColumn++)
                {
                    (augmented[column, currentColumn],
                     augmented[pivotRow, currentColumn]) =
                        (augmented[pivotRow, currentColumn],
                         augmented[column, currentColumn]);
                }
            }

            var pivot = augmented[column, column];

            for (var currentColumn = column;
                 currentColumn < 4;
                 currentColumn++)
            {
                augmented[column, currentColumn] /= pivot;
            }

            for (var row = 0; row < 3; row++)
            {
                if (row == column)
                    continue;

                var factor = augmented[row, column];

                for (var currentColumn = column;
                     currentColumn < 4;
                     currentColumn++)
                {
                    augmented[row, currentColumn] -=
                        factor * augmented[column, currentColumn];
                }
            }
        }

        for (var i = 0; i < 3; i++)
        {
            solution[i] = augmented[i, 3];
            if (!double.IsFinite(solution[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Линейная регрессия y = a + b*x.
    /// </summary>
    public static bool TryFitLine(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        out double intercept,
        out double slope)
    {
        intercept = 0;
        slope = 0;

        if (x.Count < 2 || x.Count != y.Count)
            return false;

        var xMean = x.Average();
        var yMean = y.Average();

        var numerator = 0.0;
        var denominator = 0.0;

        for (var i = 0; i < x.Count; i++)
        {
            var dx = x[i] - xMean;
            numerator += dx * (y[i] - yMean);
            denominator += dx * dx;
        }

        if (denominator <= Epsilon)
            return false;

        slope = numerator / denominator;
        intercept = yMean - slope * xMean;

        return double.IsFinite(intercept)
               && double.IsFinite(slope);
    }

    public static List<double> Smooth(
        IReadOnlyList<double> values,
        int radius)
    {
        if (values.Count == 0)
            return [];

        radius = Math.Max(0, radius);

        if (radius == 0)
            return values.ToList();

        var result = new List<double>(values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var from = Math.Max(0, i - radius);
            var to = Math.Min(values.Count - 1, i + radius);

            var sum = 0.0;
            var count = 0;

            for (var j = from; j <= to; j++)
            {
                sum += values[j];
                count++;
            }

            result.Add(sum / count);
        }

        return result;
    }

    public static double Median(IEnumerable<double> source)
    {
        var values = source
            .Where(double.IsFinite)
            .OrderBy(value => value)
            .ToList();

        if (values.Count == 0)
            return 0;

        var middle = values.Count / 2;

        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    public static double Percentile(
        IReadOnlyList<double> source,
        double percentile)
    {
        var values = source
            .Where(double.IsFinite)
            .OrderBy(value => value)
            .ToList();

        if (values.Count == 0)
            return 0;

        percentile = Math.Clamp(percentile, 0, 1);

        var position = (values.Count - 1) * percentile;
        var left = (int)Math.Floor(position);
        var right = Math.Min(values.Count - 1, left + 1);
        var fraction = position - left;

        return values[left]
               + (values[right] - values[left]) * fraction;
    }

    public static double StandardDeviation(
        IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return 0;

        var mean = values.Average();
        var sum = values.Sum(value =>
        {
            var delta = value - mean;
            return delta * delta;
        });

        return Math.Sqrt(sum / values.Count);
    }

    public static double CoefficientOfVariation(
        IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return double.PositiveInfinity;

        var mean = Math.Abs(values.Average());
        if (mean <= Epsilon)
            return double.PositiveInfinity;

        return StandardDeviation(values) / mean;
    }

    public static double Round(double value, int digits)
    {
        return Math.Round(
            value,
            digits,
            MidpointRounding.AwayFromZero);
    }

    public static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(
                    value,
                    DateTimeKind.Local)
                .ToUniversalTime()
        };
    }

    public static double SecondsBetween(
        DateTime leftUtc,
        DateTime rightUtc)
    {
        return (rightUtc - leftUtc).TotalSeconds;
    }

    private static double AbsoluteSecondsBetween(
        DateTime leftUtc,
        DateTime rightUtc)
    {
        return Math.Abs(
            SecondsBetween(leftUtc, rightUtc));
    }
}
