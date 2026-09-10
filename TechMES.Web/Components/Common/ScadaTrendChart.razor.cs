using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Radzen;
using Radzen.Blazor;
using TechMES.Contracts.Param;
using TechMES.Web.Clients;
using TechMES.Web.Settings;

namespace TechMES.Web.Components.Common;

public partial class ScadaTrendChart : IAsyncDisposable
{
    private const int MaxSeries = 8, MaxStoredPoints = 90_001, RequestChunkMinutes = 60, RequestTimeoutSeconds = 30;
    private static readonly string[] Palette = ["var(--rz-primary)", "#00a65a", "#e69500", "#e91e63", "#9263d9", "#00a8b5", "#795548", "#607d8b"];

    [Inject] private ParamApiClient ParamApi { get; set; } = default!;
    [Inject] private IOptions<ParamUiOptions> ParamOptions { get; set; } = default!;

    [Parameter] public IReadOnlyList<ScadaTrendSeries> Series { get; set; } = [];
    [Parameter] public bool AutoScale { get; set; } = true;
    [Parameter] public double? Minimum { get; set; }
    [Parameter] public double? Maximum { get; set; }
    [Parameter] public bool ShowDataLabels { get; set; } = true;
    [Parameter] public int Height { get; set; } = 360;

    private readonly CancellationTokenSource _cts = new();
    private RadzenChart? _chart;
    private PeriodicTimer? _timer;
    private Task? _pollTask;
    private ScadaTrendSeries[] _configuration = [];
    private List<SeriesState> _series = [];

    private DateTime _selectedDate, _dayFromUtc, _dayToUtc, _toUtc;
    private bool _loading, _disposed, _reloadChart, _live = true;
    private int _version, _navigatorVersion, _windowMinutes;
    private double _viewStart, _viewEnd = 1;
    private double? _navigatorStart;

    private string _message = "", _configurationError = "", _analysisTag = "";
    private bool _showAnalysis, _linear, _polynomial, _movingAverage, _mean, _median, _mode;
    private int _order = 2, _period = 6;

    // Одинаковая общая шкала времени для всех серий графика и Navigator.
    private DateTime AxisFrom => TimeBounds.From;
    private DateTime AxisTo => TimeBounds.To;
    private bool CanNavigate => _series.SelectMany(s => s.Navigator).Select(p => p.Time).Distinct().Take(2).Count() > 1;

    private (DateTime From, DateTime To) TimeBounds
    {
        get
        {
            var loaded = _series.Where(s => s.History.Count > 0).ToArray();

            if (loaded.Length == 0)
                return (_dayFromUtc, _toUtc == default ? _dayToUtc : _toUtc);

            var from = loaded.Min(s => s.History[0].Time);
            var to = loaded.Max(s => s.History[^1].Time);

            if (from < to)
                return (from, to);

            // Для единственной временной точки даём шкале ненулевую ширину.
            return (from > _dayFromUtc ? from.AddSeconds(-1) : from, to.AddSeconds(1) < _dayToUtc ? to.AddSeconds(1) : _dayToUtc);
        }
    }

    private bool HasFixedScale => !AutoScale && Minimum.HasValue && Maximum.HasValue && double.IsFinite(Minimum.Value) && double.IsFinite(Maximum.Value) && Minimum.Value < Maximum.Value;

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
    private static string FormatAxisTime(object value) => value is DateTime time ? AsUtc(time).ToLocalTime().ToString("HH:mm") : "";
    private static string FormatNavigatorTime(object value) => value is DateTime time ? AsUtc(time).ToLocalTime().ToString("dd.MM HH:mm") : "";

    protected override void OnInitialized()
    {
        _windowMinutes = Math.Clamp(ParamOptions.Value.TrendWindowMinutes, 1, 240);
        BeginDay(DateTime.Today);
    }

    protected override async Task OnParametersSetAsync()
    {
        var configuration = Series.Select(s => s with { TagName = s.TagName.Trim() }).ToArray();

        _configurationError = configuration.Length > MaxSeries ? $"A chart supports up to {MaxSeries} series." :
            configuration.Any(s => s.TagName.Length == 0) ? "Every series requires a tag name." :
            configuration.Select(s => s.TagName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Length ? "Series tag names must be unique." :
            configuration.Any(s => !Enum.IsDefined(s.Style) || !double.IsFinite(s.StrokeWidth) || s.StrokeWidth < 0.5 || s.StrokeWidth > 10) ? "Invalid series style or stroke width." : "";

        if (_configurationError.Length > 0)
        {
            _version++;
            _configuration = [];
            _series = [];
            _live = false;
            return;
        }

        if (!_configuration.SequenceEqual(configuration))
        {
            _configuration = configuration;
            _series = configuration.Select((options, index) => new SeriesState(options, Palette[index])).ToList();
            _analysisTag = _series.FirstOrDefault()?.Tag ?? "";
            BeginDay(DateTime.Today);
            _live = true;
            await LoadAsync(reset: true);
        }

        if (_timer is null && !_disposed && _series.Count > 0)
        {
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            _pollTask = PollAsync();
        }
    }

    private static (DateTime From, DateTime To) GetDayBounds(DateTime date, TimeZoneInfo zone)
    {
        var midnight = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeZoneInfo.ConvertTimeToUtc(midnight.AddDays(1), zone));
    }

    private void BeginDay(DateTime date)
    {
        _selectedDate = date.Date;
        (_dayFromUtc, _dayToUtc) = GetDayBounds(_selectedDate, TimeZoneInfo.Local);
        _version++;
        _toUtc = default;
        _message = "";
        _navigatorStart = null;
        _viewStart = 0;
        _viewEnd = 1;

        foreach (var series in _series)
        {
            series.History = [];
            series.Points = [];
            series.Navigator = [];
            series.Message = series.Options.TrendAvailable == false ? "No trend is configured." : "";
            series.Supported = series.Options.TrendAvailable;
        }
    }

    private async Task OnDateChangedAsync(DateTime? date)
    {
        if (_loading || _disposed || !date.HasValue || date.Value.Date > DateTime.Today) return;

        BeginDay(date.Value);
        _live = _selectedDate == DateTime.Today;
        await LoadAsync(reset: true);
    }

    private async Task GoLiveAsync()
    {
        if (_loading || _disposed) return;

        if (_selectedDate != DateTime.Today)
            BeginDay(DateTime.Today);

        _live = true;
        await LoadAsync(reset: _toUtc == default);
    }

    private async Task PollAsync()
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(_cts.Token))
            {
                await InvokeAsync(async () =>
                {
                    if (_disposed || _loading || !_live || _configurationError.Length > 0 || !_series.Any(s => s.Supported != false)) return;

                    if (_selectedDate != DateTime.Today)
                        BeginDay(DateTime.Today);

                    await LoadAsync(reset: _toUtc == default);

                    if (!_disposed)
                        StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private async Task LoadAsync(bool reset = false)
    {
        if (_loading || _disposed || _configurationError.Length > 0 || _series.Count == 0) return;

        _loading = true;

        var version = _version;
        var followLive = _live;
        var now = DateTime.UtcNow;
        var to = now < _dayToUtc ? now : _dayToUtc;
        var from = reset || _toUtc == default ? _dayFromUtc : _toUtc.AddSeconds(-5);

        if (from < _dayFromUtc)
            from = _dayFromUtc;

        var activeTag = "";

        try
        {
            if (from >= to) return;

            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            requestCts.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

            var updates = new List<(SeriesState Series, bool Supported, List<TrendPoint> History)>();

            foreach (var series in _series.ToArray())
            {
                activeTag = series.Tag;

                if (series.Options.TrendAvailable == false || (!reset && series.Supported == false))
                    continue;

                var response = await ReadRangeAsync(series.Tag, from, to, _dayToUtc, version, requestCts.Token);
                var existing = reset ? Enumerable.Empty<TrendPoint>() : series.History;

                var history = response.Supported ? existing.Concat(response.Points.Select(p => new TrendPoint { Time = AsUtc(p.Time), Value = p.Value }))
                    .Where(p => p.Time >= _dayFromUtc && p.Time <= to && p.Time < _dayToUtc)
                    .GroupBy(p => p.Time)
                    .Select(g => g.Last())
                    .OrderBy(p => p.Time)
                    .Take(MaxStoredPoints + 1)
                    .ToList() : [];

                if (history.Count > MaxStoredPoints)
                    throw new InvalidDataException();

                updates.Add((series, response.Supported, history));
            }

            if (_disposed || version != _version || (followLive && !_live)) return;

            // Применяем результаты вместе, чтобы серии не получили разные временные окна.
            foreach (var update in updates)
            {
                update.Series.Supported = update.Supported;
                update.Series.History = update.History;
                update.Series.Message = !update.Supported ? "No trend is configured." : update.History.Count == 0 ? "No data for the selected day." : "";
            }

            _toUtc = to;

            foreach (var series in _series)
                series.Navigator = ReducePoints(series.History, Math.Min(600, 1600 / _series.Count));

            _navigatorVersion++;
            SetVisibleRange(_selectedDate == DateTime.Today ? AxisTo.AddMinutes(-_windowMinutes) : AxisFrom, AxisTo);
            RefreshChartPoints();
            _message = "";
        }
        catch (Exception) when (_disposed || version != _version) { }
        catch (Exception error)
        {
            _live = false;

            var reason = error is InvalidDataException ? "Too many trend samples." : error is OperationCanceledException ? "Trend request timed out." : "Trend data could not be loaded.";
            _message = $"{activeTag}: {reason} Select a date again or Live.";
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task<ParamTrendResponse> ReadRangeAsync(string tag, DateTime from, DateTime to, DateTime dayEnd, int version, CancellationToken cancellationToken)
    {
        var samples = new Dictionary<DateTime, ParamTrendPointDto>();

        for (var cursor = from; cursor < to;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (version != _version)
                throw new OperationCanceledException();

            var end = cursor.AddMinutes(RequestChunkMinutes);

            if (end > to)
                end = to;

            var response = await ParamApi.GetTagTrendAsync(tag, cursor, end, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (version != _version)
                throw new OperationCanceledException();

            if (!response.Supported)
                return response;

            foreach (var point in response.Points)
            {
                var time = AsUtc(point.Time);

                if (!double.IsFinite(point.Value) || time < cursor || time > end || time >= dayEnd)
                    continue;

                samples[time] = new ParamTrendPointDto { Time = time, Value = point.Value };

                if (samples.Count > MaxStoredPoints)
                    throw new InvalidDataException();
            }

            cursor = end;
        }

        return new ParamTrendResponse { Supported = true, Points = samples.Values.ToList() };
    }

    private void ToggleAnalysis() => _showAnalysis = !_showAnalysis;
    private void PauseLive() => _live = false;

    private void OnNavigatorStartChanged(double value)
    {
        _navigatorStart = value;
        _live = false;
    }

    private void OnNavigatorEndChanged(double value)
    {
        OnViewChanged(new ChartViewChangeEventArgs { ViewStart = _navigatorStart ?? _viewStart, ViewEnd = value });
        _navigatorStart = null;
    }

    private void OnViewChanged(ChartViewChangeEventArgs args)
    {
        if (Math.Abs(args.ViewStart - _viewStart) < 0.000001 && Math.Abs(args.ViewEnd - _viewEnd) < 0.000001) return;

        _viewStart = Math.Clamp(args.ViewStart, 0, 1);
        _viewEnd = Math.Clamp(args.ViewEnd, _viewStart, 1);
        _live = false;

        RefreshChartPoints();
    }

    private void SetVisibleRange(DateTime from, DateTime to)
    {
        var ticks = (AxisTo - AxisFrom).Ticks;

        if (ticks <= 0)
        {
            _viewStart = 0;
            _viewEnd = 1;
            return;
        }

        _viewStart = Math.Clamp((double)(from - AxisFrom).Ticks / ticks, 0, 1);
        _viewEnd = Math.Clamp((double)(to - AxisFrom).Ticks / ticks, _viewStart, 1);
    }

    private void RefreshChartPoints()
    {
        var from = AxisFrom.AddTicks((long)((AxisTo - AxisFrom).Ticks * _viewStart));
        var to = AxisFrom.AddTicks((long)((AxisTo - AxisFrom).Ticks * _viewEnd));

        foreach (var series in _series)
        {
            var first = series.History.FindIndex(p => p.Time >= from);

            if (first < 0)
                first = series.History.Count;

            var end = first;

            while (end < series.History.Count && series.History[end].Time <= to)
                end++;

            series.Points = ReducePoints(series.History.GetRange(first, end - first), Math.Min(1200, 4800 / _series.Count) - 2);

            if (first > 0)
                series.Points.Insert(0, series.History[first - 1]);

            if (end < series.History.Count)
                series.Points.Add(series.History[end]);
        }

        _reloadChart = true;
    }

    private static List<TrendPoint> ReducePoints(List<TrendPoint> source, int limit)
    {
        if (source.Count <= limit)
            return source.ToList();

        var result = new List<TrendPoint>(limit) { source[0] };
        var buckets = (limit - 2) / 2;
        var innerCount = source.Count - 2;

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = 1 + (int)((long)bucket * innerCount / buckets);
            var end = 1 + (int)((long)(bucket + 1) * innerCount / buckets);
            var min = start;
            var max = start;

            for (var index = start + 1; index < end; index++)
            {
                if (source[index].Value < source[min].Value)
                    min = index;

                if (source[index].Value > source[max].Value)
                    max = index;
            }

            result.Add(source[Math.Min(min, max)]);

            if (min != max)
                result.Add(source[Math.Max(min, max)]);
        }

        result.Add(source[^1]);
        return result;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_reloadChart && _chart is not null && !_disposed)
        {
            _reloadChart = false;
            await _chart.Reload();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _cts.Cancel();
        _timer?.Dispose();

        if (_pollTask is not null)
            await _pollTask;

        _cts.Dispose();
    }

    public sealed class TrendPoint
    {
        public DateTime Time { get; set; }
        public double Value { get; set; }
    }

    private sealed class SeriesState(ScadaTrendSeries options, string fallbackColor)
    {
        public ScadaTrendSeries Options { get; } = options;
        public string Tag => Options.TagName;
        public string Name => string.IsNullOrWhiteSpace(Options.Title) ? Tag : Options.Title;
        public string Color => string.IsNullOrWhiteSpace(Options.Color) ? fallbackColor : Options.Color;
        public bool? Supported { get; set; } = options.TrendAvailable;
        public string Message { get; set; } = "";
        public List<TrendPoint> History { get; set; } = [];
        public List<TrendPoint> Points { get; set; } = [];
        public List<TrendPoint> Navigator { get; set; } = [];
    }
}