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

    /// <summary>Читает сутки от новых данных к старым; публикует готовые порции и умеет продолжать неполный буфер.</summary>
    internal static async Task<ParamTrendResponse> LoadDayAsync(DateTime date, ParamTrendResponse? cache, int chunkMinutes, Func<DateTime, DateTime, CancellationToken, Task<ParamTrendResponse>> read, CancellationToken ct, Func<ParamTrendResponse, Task>? onProgress = null)
    {
        var (from, dayEnd) = DayBounds(date);
        var now = DateTime.UtcNow;
        var to = now < dayEnd ? now : dayEnd;

        if (to <= from)
            return new ParamTrendResponse { Supported = true, FromUtc = from, ToUtc = dayEnd, Message = "No data for the selected day." };

        if (cache?.Supported != true || cache.FromUtc < from || cache.FromUtc >= to || cache.ToUtc > to || cache.ToUtc < cache.FromUtc)
            cache = null;

        // Ограничения действуют отдельно на одну порцию и на всю операцию.
        // После общего таймаута уже показанный участок остаётся доступным.
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(TimeSpan.FromMinutes(2));

        var token = operation.Token;
        var points = new Dictionary<(string Series, DateTime Time), ParamTrendPointDto>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ParamTrendResponse? latest = cache;
        var coveredFrom = cache?.FromUtc ?? to;
        var coveredTo = cache?.ToUtc ?? to;

        if (cache is not null)
            AddPoints(cache.Points, from, to, dayEnd, points, counts);

        // Читает одну порцию с отдельным таймаутом, не меняя настройки внешнего источника.
        async Task<ParamTrendResponse> ReadChunkAsync(DateTime start, DateTime end)
        {
            token.ThrowIfCancellationRequested();

            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await read(start, end, request.Token);
            request.Token.ThrowIfCancellationRequested();

            if (response.Series.Count > MaxSeries)
                throw new InvalidDataException("Too many trend series.");

            return response;
        }

        // Сначала обновляем конец уже загруженного участка.
        if (cache is not null)
        {
            var tailFrom = cache.ToUtc.AddSeconds(-5);

            if (tailFrom < coveredFrom)
                tailFrom = coveredFrom;

            while (tailFrom < to)
            {
                var end = tailFrom.AddMinutes(Math.Clamp(chunkMinutes, 1, 240));

                if (end > to)
                    end = to;

                latest = await ReadChunkAsync(tailFrom, end);

                if (!latest.Supported)
                    return latest;

                if (!SameScale(cache, latest))
                    return await LoadDayAsync(date, null, chunkMinutes, read, token, onProgress);

                AddPoints(latest.Points, tailFrom, end, dayEnd, points, counts);

                coveredTo = end;
                tailFrom = end;

                if (onProgress is not null)
                    await onProgress(BuildResult(coveredFrom > from || coveredTo < to));
            }
        }

        // Историю подгружаем назад: последняя порция появляется первой.
        while (coveredFrom > from)
        {
            var end = coveredFrom;
            var start = end.AddMinutes(-Math.Clamp(chunkMinutes, 1, 240));

            if (start < from)
                start = from;

            var response = await ReadChunkAsync(start, end);

            if (!response.Supported)
                return response;

            if (latest is not null && !SameScale(latest, response))
                throw new InvalidDataException("Trend scales changed during loading. Select Live or the date again.");

            latest = response;
            AddPoints(response.Points, start, end, dayEnd, points, counts);
            coveredFrom = start;

            // Передаём новый объект и отдельный список: показанный буфер больше не изменяется.
            if (onProgress is not null)
                await onProgress(BuildResult(coveredFrom > from));

            token.ThrowIfCancellationRequested();
        }

        return BuildResult(false);

        // FromUtc обозначает фактически загруженное начало, чтобы Live мог продолжить после ошибки.
        ParamTrendResponse BuildResult(bool loading) => new()
        {
            EquipmentName = latest?.EquipmentName ?? "",
            TypeGroup = latest?.TypeGroup ?? default,
            Supported = latest?.Supported == true,
            FromUtc = coveredFrom,
            ToUtc = coveredTo,
            AxisYMin = latest?.AxisYMin,
            AxisYMax = latest?.AxisYMax,
            Series = latest?.Series ?? [],
            Points = points.Values.OrderBy(p => p.Time).ThenBy(p => p.Series).ToList(),
            Message = loading ? "Loading day history..." : points.Count == 0 ? "No data for the selected day." : ""
        };
    }

    /// <summary>Меняет сообщение новым снимком, не изменяя уже переданные компоненту данные.</summary>
    internal static ParamTrendResponse WithMessage(ParamTrendResponse source, string message) => new()
    {
        EquipmentName = source.EquipmentName,
        TypeGroup = source.TypeGroup,
        Supported = source.Supported,
        FromUtc = source.FromUtc,
        ToUtc = source.ToUtc,
        AxisYMin = source.AxisYMin,
        AxisYMax = source.AxisYMax,
        Series = source.Series,
        Points = source.Points,
        Message = message
    };

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