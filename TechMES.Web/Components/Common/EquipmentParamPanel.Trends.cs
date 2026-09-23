using System.Text.Json;
using Radzen;
using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

public partial class EquipmentParamPanel
{
    private object? _paramRefreshOperation;
    private string _trendOwner = "", _tuneBufferSettings = "";
    private DateTime _graphTrendDate = DateTime.Today, _tuneTrendDate = DateTime.Today;
    private DateTime? _graphVisibleFromUtc, _graphVisibleToUtc;

    /// <summary>Не переносит дату и диапазон между разным оборудованием.</summary>
    private void EnsureTrendOwner(string equipmentName)
    {
        if (_trendOwner == equipmentName)
            return;

        _trendOwner = equipmentName;
        _graphTrendDate = _tuneTrendDate = DateTime.Today;
        _graphVisibleFromUtc = _graphVisibleToUtc = null;
        _tuneBufferSettings = "";
    }

    /// <summary>Загружает Graph за сутки; в Live дополняет только конец буфера.</summary>
    private async Task LoadGraphTrendAsync(string equipmentName, CancellationToken ct, bool reload = false)
    {
        EnsureTrendOwner(equipmentName);

        var date = IsGraphLive ? DateTime.Today : _graphTrendDate;
        var published = false;

        // Показываем первую готовую порцию, не ожидая всей истории от полуночи.
        async Task PublishAsync(ParamTrendResponse trend)
        {
            ct.ThrowIfCancellationRequested();

            if (Equipment?.Name != equipmentName || !IsActive || _isDisposed)
                return;

            if (_trend is null || _trend.FromUtc.ToLocalTime().Date != date.Date)
            {
                _graphVisibleFromUtc = _graphVisibleToUtc = null;
                _chartViewVersion++;
            }

            _trend = trend;
            _graphTrendDate = date;
            published = true;

            await InvokeAsync(StateHasChanged);
        }

        try
        {
            var trend = await ScadaTrendData.LoadDayAsync(date, reload ? null : _trend, _trendHistoryMinutes, (from, to, token) => ParamApi.GetTrendAsync(equipmentName, _trendHistoryMinutes, from, to, token), ct, PublishAsync);
            await PublishAsync(trend);
        }
        catch
        {
            // Не запускаем полную повторную загрузку каждые пять секунд после таймаута.
            if (published && !ct.IsCancellationRequested && Equipment?.Name == equipmentName && _trend is not null)
            {
                _fixedTrendToUtc = _trend.ToUtc;
                _trend = ScadaTrendData.WithMessage(_trend, "History loading stopped. Loaded data is available. Select Live or the date to retry.");
            }

            throw;
        }
    }

    /// <summary>Определяет настройки источников, от которых зависит буфер Tune.</summary>
    private static string TuneSourceKey(ParamTuneSettingsResponse? settings) => settings is null ? "" : JsonSerializer.Serialize(new { settings.Pv, settings.Sp, settings.PvMin, settings.PvMax, settings.SpMin, settings.SpMax });

    /// <summary>Загружает Tune по фиксированному снимку настроек, сохраняя RawValue для PID.</summary>
    private async Task LoadTuneAsync(string equipmentName, CancellationToken ct, bool reload = false)
    {
        EnsureTrendOwner(equipmentName);

        var editor = _hasTuneSettingsLoadedFromStore && _tune?.Settings.EquipmentName == equipmentName ? _tune.Settings : null;
        var key = TuneSourceKey(editor);
        var draft = editor is null ? null : JsonSerializer.Deserialize<ParamTuneSettingsResponse>(JsonSerializer.Serialize(editor));
        var cache = !reload && key == _tuneBufferSettings ? _tuneTrend : null;
        var date = IsTuneLive ? DateTime.Today : _tuneTrendDate;

        ParamTuneRuntimeResponse? latest = null;
        var published = false;

        // Все порции используют одни PV/SP и шкалы, даже если оператор редактирует форму.
        async Task<ParamTrendResponse> ReadAsync(DateTime from, DateTime to, CancellationToken token)
        {
            latest = await ParamApi.GetTuneAsync(equipmentName, _trendHistoryMinutes, from, to, token, draft);

            draft ??= JsonSerializer.Deserialize<ParamTuneSettingsResponse>(JsonSerializer.Serialize(latest.Settings));
            editor ??= JsonSerializer.Deserialize<ParamTuneSettingsResponse>(JsonSerializer.Serialize(draft));

            return latest.Trend;
        }

        // Редактор и снимок запроса являются разными объектами: ввод не меняет выполняемые запросы.
        async Task PublishAsync(ParamTrendResponse trend)
        {
            ct.ThrowIfCancellationRequested();

            if (latest is null || Equipment?.Name != equipmentName || !IsActive || _isDisposed)
                return;

            if (TuneSourceKey(editor) != TuneSourceKey(draft))
                throw new InvalidOperationException("Tune settings changed during loading. Check the tags again.");

            if (_tuneTrend is null || _tuneTrend.FromUtc.ToLocalTime().Date != date.Date)
            {
                _tuneVisibleFromUtc = _tuneVisibleToUtc = null;
                _tuneChartViewVersion++;
            }

            latest.Settings = editor ?? latest.Settings;
            latest.Trend = trend;

            _tune = latest;
            _tuneTrend = trend;
            _tuneTrendDate = date;
            _hasTuneSettingsLoadedFromStore = true;
            _tuneBufferSettings = TuneSourceKey(draft);

            if (!string.IsNullOrWhiteSpace(latest.Message))
                _tuneStatusStyle = latest.Supported ? AlertStyle.Info : AlertStyle.Warning;

            published = true;
            await InvokeAsync(StateHasChanged);
        }

        try
        {
            var trend = await ScadaTrendData.LoadDayAsync(date, cache, _trendHistoryMinutes, ReadAsync, ct, PublishAsync);
            await PublishAsync(trend);
        }
        catch
        {
            // Сохраняем показанную историю и приостанавливаем её автоматическую подгрузку.
            if (published && !ct.IsCancellationRequested && Equipment?.Name == equipmentName && _tuneTrend is not null)
            {
                _fixedTuneToUtc = _tuneTrend.ToUtc;
                _tuneTrend = ScadaTrendData.WithMessage(_tuneTrend, "History loading stopped. Loaded data is available. Select Live or the date to retry.");

                if (_tune is not null)
                    _tune.Trend = _tuneTrend;
            }

            throw;
        }
    }

    /// <summary>Сохраняет реальный диапазон Graph и останавливает polling истории.</summary>
    private Task OnGraphVisibleWindowChangedAsync(TrendVisibleWindowChangedEventArgs range)
    {
        if (range.ViewKey != ChartViewKey)
            return Task.CompletedTask;

        _graphVisibleFromUtc = range.FromUtc;
        _graphVisibleToUtc = range.ToUtc;
        _fixedTrendToUtc = range.IsLive ? null : range.ToUtc;

        return Task.CompletedTask;
    }

    /// <summary>Обрабатывает выбор дня для основного графика.</summary>
    private Task OnGraphDateSelectedAsync(DateTime date) => SelectTrendDayAsync(date, false);

    /// <summary>Обрабатывает выбор дня для PID Tune.</summary>
    private Task OnTuneDateSelectedAsync(DateTime date) => SelectTrendDayAsync(date, true);

    /// <summary>Меняет день порциями; при ошибке сохраняет загруженную часть или предыдущий график.</summary>
    private async Task SelectTrendDayAsync(DateTime date, bool tune, bool reload = true)
    {
        if (Equipment is null || !IsActive || _isDisposed || _isRefreshing || _isTuneChecking || _isTuneSaving || date.Date > DateTime.Today)
            return;

        var equipmentName = Equipment.Name;
        var ct = GetActiveLoadToken();

        EnsureTrendOwner(equipmentName);

        var previousDate = tune ? _tuneTrendDate : _graphTrendDate;
        var previousFixed = tune ? _fixedTuneToUtc : _fixedTrendToUtc;
        var previousTrend = tune ? _tuneTrend : _trend;
        var (_, end) = ScadaTrendData.DayBounds(date);

        DateTime? fixedTo = date.Date == DateTime.Today ? null : end.AddTicks(-1);

        var operation = new object();
        _paramRefreshOperation = operation;
        _isRefreshing = true;

        try
        {
            if (tune)
            {
                _tuneTrendDate = date.Date;
                _fixedTuneToUtc = fixedTo;

                await LoadTuneAsync(equipmentName, ct, reload);

                if (!ReferenceEquals(_paramRefreshOperation, operation) || Equipment?.Name != equipmentName || !IsActive || _isDisposed)
                    return;

                _tuneVisibleFromUtc = _tuneVisibleToUtc = null;
                _tuneChartViewVersion++;
            }
            else
            {
                _graphTrendDate = date.Date;
                _fixedTrendToUtc = fixedTo;

                await LoadGraphTrendAsync(equipmentName, ct, reload);

                if (!ReferenceEquals(_paramRefreshOperation, operation) || Equipment?.Name != equipmentName || !IsActive || _isDisposed)
                    return;

                _graphVisibleFromUtc = _graphVisibleToUtc = null;
                _chartViewVersion++;
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_paramRefreshOperation, operation) || Equipment?.Name != equipmentName || !IsActive || _isDisposed)
                return;

            // Если уже показана часть нового дня, сохраняем её для просмотра и продолжения загрузки.
            if (tune && ReferenceEquals(_tuneTrend, previousTrend))
            {
                _tuneTrendDate = previousDate;
                _fixedTuneToUtc = previousFixed;
                _tuneTrend = previousTrend;
            }
            else if (!tune && ReferenceEquals(_trend, previousTrend))
            {
                _graphTrendDate = previousDate;
                _fixedTrendToUtc = previousFixed;
                _trend = previousTrend;
            }

            _statusStyle = AlertStyle.Warning;
            _statusText = ex is OperationCanceledException ? "Trend request timed out. Loaded data has been kept." : "Cannot load the selected day: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_paramRefreshOperation, operation))
                _isRefreshing = false;
        }
    }
}