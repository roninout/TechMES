using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

public partial class ScadaTrendChart
{
    [Parameter] public bool ExternalData { get; set; }
    [Parameter] public ParamTrendResponse? Trend { get; set; }
    [Parameter] public string ViewKey { get; set; } = "";
    [Parameter] public string? Unit { get; set; }
    [Parameter] public ScadaTrendStyle RenderStyle { get; set; } = ScadaTrendStyle.Area;
    [Parameter] public IReadOnlyDictionary<string, string>? SeriesDisplayNames { get; set; }
    [Parameter] public DateTime? VisibleFromUtc { get; set; }
    [Parameter] public DateTime? VisibleToUtc { get; set; }
    [Parameter] public bool ExternalLive { get; set; } = true;
    [Parameter] public bool IsBusy { get; set; }
    [Parameter] public EventCallback<DateTime> OnDateSelected { get; set; }
    [Parameter] public EventCallback OnGoLive { get; set; }
    [Parameter] public EventCallback<TrendVisibleWindowChangedEventArgs> OnVisibleWindowChanged { get; set; }

    [Inject] private IJSRuntime TrendJs { get; set; } = default!;

    private ElementReference _root;
    private IJSObjectReference? _layoutModule, _layoutObserver;
    private ParamTrendResponse? _appliedTrend;
    private string? _appliedViewKey;
    private bool _notifyRange;
    private (DateTime From, DateTime To, string Key, bool Live)? _notifiedRange;

    private bool Busy => _loading || IsBusy;
    private double? ScaleMinimum => Minimum ?? (ExternalData ? Trend?.AxisYMin : null);
    private double? ScaleMaximum => Maximum ?? (ExternalData ? Trend?.AxisYMax : null);

    /// <summary>Применяет внешний буфер без собственных запросов и дополнительного polling.</summary>
    private void ApplyExternalTrend()
    {
        _live = ExternalLive;

        if (Trend is not { } trend)
        {
            _series = [];
            _appliedTrend = null;
            _message = "Trend data has not been loaded. Select a date or Live.";
            return;
        }

        var definitions = trend.Series.Count > 0 ? trend.Series : trend.Points.Select(p => p.Series).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new ParamTrendItemDto { Name = name }).ToList();

        var configuration = definitions.Select(item => Series.FirstOrDefault(s => s.TagName.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) ?? new ScadaTrendSeries
        {
            TagName = item.Name,
            Title = SeriesDisplayNames?.TryGetValue(item.Name, out var title) == true ? title : item.Name,
            Color = item.Color,
            Unit = Unit ?? "",
            Style = RenderStyle,
            TrendAvailable = trend.Supported
        }).ToArray();

        _configurationError = configuration.Length > MaxSeries || trend.Points.Count > MaxSeries * MaxStoredPoints ? "Too many trend samples or series." : "";

        if (_configurationError.Length > 0)
            return;

        var changedView = _appliedViewKey != ViewKey;

        if (ReferenceEquals(_appliedTrend, trend) && !changedView && _configuration.SequenceEqual(configuration))
            return;

        var previous = GetVisibleRange();
        var groups = trend.Points.GroupBy(p => p.Series, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (groups.Values.Any(points => points.Count > MaxStoredPoints))
        {
            _configurationError = "Too many trend samples.";
            return;
        }

        _configuration = configuration;

        _series = configuration.Select((options, index) =>
        {
            groups.TryGetValue(options.TagName, out var points);

            var history = (points ?? [])
                .Where(p => double.IsFinite(p.Value))
                .Select(p => new TrendPoint { Time = ScadaTrendData.EquipmentUtc(p.Time), Value = p.Value, RawValue = p.RawValue, Quality = p.Quality })
                .GroupBy(p => p.Time)
                .Select(g => g.Last())
                .OrderBy(p => p.Time)
                .ToList();

            return new SeriesState(options, Palette[index])
            {
                Supported = trend.Supported,
                History = history,
                Message = history.Count == 0 ? "No data for the selected day." : ""
            };
        }).ToList();

        _appliedTrend = trend;
        _appliedViewKey = ViewKey;
        _selectedDate = trend.FromUtc.ToLocalTime().Date;
        (_dayFromUtc, _dayToUtc) = ScadaTrendData.DayBounds(_selectedDate);
        _toUtc = trend.ToUtc;
        _message = trend.Message ?? "";

        if (!_series.Any(s => s.Tag == _analysisTag))
            _analysisTag = _series.FirstOrDefault()?.Tag ?? "";

        foreach (var series in _series)
            series.Navigator = ReducePoints(series.History, Math.Min(600, 1600 / _series.Count));

        var from = changedView ? VisibleFromUtc ?? (_live ? AxisTo.AddMinutes(-_windowMinutes) : AxisFrom) : _live ? AxisTo.AddMinutes(-_windowMinutes) : previous.From;
        var to = changedView ? VisibleToUtc ?? AxisTo : _live ? AxisTo : previous.To;

        SetVisibleRange(from, to);
        _navigatorVersion++;
        RefreshChartPoints();
        _notifyRange = true;
    }

    /// <summary>Возвращает реальные границы видимого участка в UTC для PID Tune.</summary>
    private (DateTime From, DateTime To) GetVisibleRange()
    {
        var (from, to) = TimeBounds;
        return (from.AddTicks((long)((to - from).Ticks * _viewStart)), from.AddTicks((long)((to - from).Ticks * _viewEnd)));
    }

    /// <summary>Передаёт диапазон и Live/History родителю; одинаковые события не повторяет.</summary>
    private async Task NotifyRangeAsync()
    {
        _notifyRange = false;

        if (_disposed || !ExternalData || !OnVisibleWindowChanged.HasDelegate || _series.Count == 0)
            return;

        var (from, to) = GetVisibleRange();
        var state = (from, to, ViewKey, _live);

        if (_notifiedRange == state)
            return;

        _notifiedRange = state;
        await OnVisibleWindowChanged.InvokeAsync(new TrendVisibleWindowChangedEventArgs(from, to, ViewKey, _live));
    }

    /// <summary>Выравнивает боковую панель оборудования по области построения Radzen.</summary>
    private async Task ObserveLayoutAsync()
    {
        if (!ExternalData || _disposed || _layoutObserver is not null || _configurationError.Length > 0)
            return;

        try
        {
            _layoutModule ??= await TrendJs.InvokeAsync<IJSObjectReference>("import", "./scadaTrendLayout.js");

            if (!_disposed)
                _layoutObserver = await _layoutModule.InvokeAsync<IJSObjectReference>("observe", _root);
        }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>Отключает наблюдатели размеров при удалении компонента.</summary>
    private async ValueTask DisposeLayoutAsync()
    {
        try
        {
            if (_layoutObserver is not null)
            {
                await _layoutObserver.InvokeVoidAsync("dispose");
                await _layoutObserver.DisposeAsync();
            }

            if (_layoutModule is not null)
                await _layoutModule.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}