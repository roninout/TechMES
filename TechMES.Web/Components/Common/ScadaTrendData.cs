using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Загружает одни локальные сутки ограниченными порциями, сохраняя исходные значения для PID Tune.
/// </summary>
internal static class ScadaTrendData
{
    internal const int MaxSeries = 8, MaxPointsPerSeries = 90_001;

    /// <summary>Equipment API возвращает старые точки без Kind в локальном времени.</summary>
    internal static DateTime EquipmentUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime() : value.ToUniversalTime();

    /// <summary>Возвращает границы локального дня в UTC с учётом перехода на летнее время.</summary>
    internal static (DateTime From, DateTime To) DayBounds(DateTime date)
    {
        var midnight = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(midnight, TimeZoneInfo.Local), TimeZoneInfo.ConvertTimeToUtc(midnight.AddDays(1), TimeZoneInfo.Local));
    }

    /// <summary>Собирает новый буфер отдельно; ошибка или отмена не изменяет предыдущий график.</summary>
    internal static async Task<ParamTrendResponse> LoadDayAsync(DateTime date, ParamTrendResponse? cache, int chunkMinutes, Func<DateTime, DateTime, CancellationToken, Task<ParamTrendResponse>> read, CancellationToken ct)
    {
        var (from, dayEnd) = DayBounds(date);
        var to = DateTime.UtcNow < dayEnd ? DateTime.UtcNow : dayEnd;

        if (to <= from)
            return new ParamTrendResponse { Supported = true, FromUtc = from, ToUtc = dayEnd, Message = "No data for the selected day." };

        if (cache?.Supported != true || cache.FromUtc != from || cache.ToUtc > to)
            cache = null;

        var points = new Dictionary<(string Series, DateTime Time), ParamTrendPointDto>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ParamTrendResponse? latest = cache;

        if (cache is not null)
            AddPoints(cache.Points, from, to, dayEnd, points, counts);

        var cursor = cache is null ? from : cache.ToUtc.AddSeconds(-5);

        if (cursor < from)
            cursor = from;

        while (cursor < to)
        {
            ct.ThrowIfCancellationRequested();

            var end = cursor.AddMinutes(Math.Clamp(chunkMinutes, 1, 240));

            if (end > to)
                end = to;

            latest = await read(cursor, end, ct);
            ct.ThrowIfCancellationRequested();

            if (!latest.Supported)
                return latest;

            if (latest.Series.Count > MaxSeries)
                throw new InvalidDataException("Too many trend series.");

            // Не объединяем точки, нормализованные по разным шкалам.
            if (cache is not null && !SameScale(cache, latest))
                return await LoadDayAsync(date, null, chunkMinutes, read, ct);

            AddPoints(latest.Points, cursor, end, dayEnd, points, counts);
            cursor = end;
        }

        return new ParamTrendResponse
        {
            EquipmentName = latest?.EquipmentName ?? "",
            TypeGroup = latest?.TypeGroup ?? default,
            Supported = latest?.Supported == true,
            FromUtc = from,
            ToUtc = to,
            AxisYMin = latest?.AxisYMin,
            AxisYMax = latest?.AxisYMax,
            Series = latest?.Series ?? [],
            Points = points.Values.OrderBy(p => p.Time).ThenBy(p => p.Series).ToList(),
            Message = points.Count == 0 ? "No data for the selected day." : ""
        };
    }

    /// <summary>Проверяет совместимость шкал предыдущего буфера и новой порции.</summary>
    private static bool SameScale(ParamTrendResponse left, ParamTrendResponse right)
    {
        return left.AxisYMin == right.AxisYMin && left.AxisYMax == right.AxisYMax && left.Series.Select(s => (s.Name, s.NativeMin, s.NativeMax)).SequenceEqual(right.Series.Select(s => (s.Name, s.NativeMin, s.NativeMax)));
    }

    /// <summary>Нормализует время, удаляет дубликаты и ограничивает объём каждой серии.</summary>
    private static void AddPoints(IEnumerable<ParamTrendPointDto> source, DateTime from, DateTime to, DateTime dayEnd, Dictionary<(string Series, DateTime Time), ParamTrendPointDto> points, Dictionary<string, int> counts)
    {
        foreach (var point in source)
        {
            var time = EquipmentUtc(point.Time);

            if (!double.IsFinite(point.Value) || time < from || time > to || time >= dayEnd)
                continue;

            var key = (point.Series.ToUpperInvariant(), time);

            if (!points.ContainsKey(key))
            {
                counts.TryGetValue(key.Item1, out var count);
                counts[key.Item1] = count + 1;

                if (counts.Count > MaxSeries || count >= MaxPointsPerSeries)
                    throw new InvalidDataException("Too many trend samples. The previous chart has been kept.");
            }

            points[key] = new ParamTrendPointDto
            {
                Series = point.Series,
                Time = time,
                Value = point.Value,
                RawValue = point.RawValue,
                Quality = point.Quality
            };
        }
    }
}