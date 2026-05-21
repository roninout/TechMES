using CtApi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Вся логика трендов в одном месте:
    /// - polling CtApi TrendData
    /// - Live/History (DevExpress Scroll/Zoom)
    /// - автоподгрузка истории слева
    /// - scaling разных серий в базовую ось
    /// - обновление VM (оси/точки/статус)
    /// </summary>
    public sealed class ParamTrendController
    {
        #region Fields

        private readonly ParamTrendVm _vm;
        private readonly Dispatcher _ui;
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;

        // Как получить контекст (из MainWindow)
        private readonly Func<(string equipName, string equipType, string equipDescription)> _resolveEquip;
        private readonly Func<object?> _getParamModel;
        private readonly Func<int> _getParamCycles;

        // caches
        private string? _trendEquipName;
        private readonly Dictionary<string, string> _trnNameByItem = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastUtcByItem = new(StringComparer.OrdinalIgnoreCase);

        // Gate: polling и history-load не должны пересекаться
        private readonly SemaphoreSlim _trendGate = new(1, 1);

        // Навигация DevExpress может стрелять очень часто
        private DateTime _lastNavUtc = DateTime.MinValue;
        private static readonly TimeSpan NavDebounce = TimeSpan.FromMilliseconds(250);

        // История
        private const int HistoryChunkMinutes = 60; // сколько добираем за раз при подходе к левому краю
        private const int HistoryKeepHours = 24;    // safety trim (0 = не резать)

        // подавление событий Scroll/Zoom, которые могут прилетать из-за programmatic changes VisualRange
        private int _navSuppressCount;
        private bool IsNavSuppressed => Volatile.Read(ref _navSuppressCount) > 0;

        private void NavSuppressEnter() => Interlocked.Increment(ref _navSuppressCount);
        private void NavSuppressExit() => Interlocked.Decrement(ref _navSuppressCount);

        // набор “разрешенных” окон (под ComboBox). Подстрой под свой список.
        private static readonly int[] WindowPresets = new[] { 5, 10, 15, 30, 60, 120, 240 };

        // чтобы игнорировать RangeChanged, которые вызваны НАШИМ же SetLiveMode (иначе будет дергать History обратно)
        private int _suppressUserRangeChanged;

        // чтобы авто-переход сработал только когда реально "дотянулись",
        // а не на каждом Zoom/Scroll пока мы уже стоим у правого края
        private bool _wasAtRightEdge;

        private static readonly TimeSpan RightEdgeTolerance = TimeSpan.FromSeconds(2);

        private bool IsSuppressed => Volatile.Read(ref _suppressUserRangeChanged) > 0;

        #endregion

        public ParamTrendController(ParamTrendVm vm, Dispatcher ui, IEquipmentService equipmentService, ICtApiService ctApiService, Func<(string equipName, string equipType, string equipDescription)> resolveEquip, Func<object?> getParamModel,Func<int> getParamCycles)
        {
            _vm = vm;
            _ui = ui;
            _equipmentService = equipmentService;
            _ctApiService = ctApiService;
            _resolveEquip = resolveEquip;
            _getParamModel = getParamModel;
            _getParamCycles = getParamCycles;

            Reset(clearPoints: true);
        }

        /// <summary>
        /// Сброс состояния тренда (оси/режим/кэш/точки).
        /// </summary>
        public void Reset(bool clearPoints)
        {
            if (clearPoints)
                _vm.Points.Clear();

            _trnNameByItem.Clear();
            _lastUtcByItem.Clear();
            _trendEquipName = null;

            _vm.IsLiveMode = true;

            var now = DateTime.Now;
            var win = Math.Max(1, _vm.WindowMinutes);

            _vm.AxisXMax = now;
            _vm.AxisXMin = now.AddMinutes(-win);

            _vm.AxisXWholeMin = _vm.AxisXMin;
            _vm.AxisXWholeMax = _vm.AxisXMax;
        }

        public void ShowChart(bool reset)
        {
            if (reset)
            {
                // 1) Сбрасываем окно отображения на дефолт (30 минут)
                _vm.WindowMinutes = 30;

                // 2) Полный reset: Live + оси + (опционально) очистка точек
                Reset(clearPoints: true);
                //SetLiveMode(resetPoints: false);
            }

            // 3) Показываем график
            _vm.IsChartVisible = true;
        }

        public void ShowSettings() => _vm.IsChartVisible = false;

        public void ToggleChart(bool resetWhenShow)
        {
            if (_vm.IsChartVisible)
                ShowSettings();
            else
                ShowChart(resetWhenShow);
        }

        /// <summary>
        /// Переключить обратно в Live режим.
        /// </summary>
        public void SetLiveMode(bool resetPoints = false)
        {
            _ = SetLiveModeAsync(resetPoints, keepSpan: null);
        }

        private async Task SetLiveModeAsync(bool resetPoints, TimeSpan? keepSpan)
        {
            try
            {
                await _trendGate.WaitAsync(CancellationToken.None);
                try
                {
                    _vm.IsLiveMode = true;

                    var now = DateTime.Now;
                    //var win = Math.Max(1, _vm.WindowMinutes);

                    // ✅ если keepSpan передали — используем его (сохранение масштаба)
                    // иначе — стандартное окно WindowMinutes
                    var span = keepSpan.GetValueOrDefault(TimeSpan.FromMinutes(Math.Max(1, _vm.WindowMinutes)));
                    if (span < TimeSpan.FromSeconds(5))
                        span = TimeSpan.FromMinutes(Math.Max(1, _vm.WindowMinutes));

                    NavSuppressEnter();
                    try
                    {
                        await _ui.InvokeAsync(() =>
                        {
                            if (resetPoints)
                            {
                                Reset(clearPoints: true);
                                return;
                            }

                            _vm.AxisXMax = now;
                            _vm.AxisXMin = now - span;
                            //_vm.AxisXMin = now.AddMinutes(-win);

                            // в Live WholeRange = VisualRange
                            _vm.AxisXWholeMin = _vm.AxisXMin;
                            _vm.AxisXWholeMax = _vm.AxisXMax;

                            // режем точки по окну
                            var minKeep = _vm.AxisXMin;
                            for (int i = _vm.Points.Count - 1; i >= 0; i--)
                                if (_vm.Points[i].Time < minKeep)
                                    _vm.Points.RemoveAt(i);
                        });
                    }
                    finally
                    {
                        NavSuppressExit();
                    }
                }
                finally
                {
                    _trendGate.Release();
                }
            }
            catch
            {
                // не ломаем UI
            }
        }

        /// <summary>
        /// Вызывается из View при Scroll/Zoom DevExpress.
        /// Переключает в History, сохраняет VisualRange и запускает автоподгрузку слева.
        /// </summary>
        public void OnUserRangeChanged(DateTime newMinLocal, DateTime newMaxLocal)
        {
            // 0) игнорируем RangeChanged, вызванные нами при SetLiveMode
            if (IsSuppressed)
                return;

            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastNavUtc < NavDebounce)
                return;

            _lastNavUtc = nowUtc;

            // 1) AutoLive: только если включено в конфиге
            //    и только когда мы "впервые" дотянулись до правого края (переход threshold false -> true)
            if (_vm.AutoLive)
            {
                var atRightEdge = IsAtRightEdge(newMaxLocal);

                if (!_vm.IsLiveMode && atRightEdge && !_wasAtRightEdge)
                {
                    _wasAtRightEdge = true;

                    // сохраняем текущий масштаб (span)
                    var span = newMaxLocal - newMinLocal;
                    if (span < TimeSpan.FromSeconds(5))
                        span = TimeSpan.FromMinutes(Math.Max(1, _vm.WindowMinutes));

                    _ = SetLiveModeAsync(resetPoints: false, keepSpan: span);
                    return;
                }

                _wasAtRightEdge = atRightEdge;
            }
            else
            {
                _wasAtRightEdge = false;
            }

            // 2) обычное поведение: любое движение пользователя переводит в History
            _vm.IsLiveMode = false;

            _ui.InvokeAsync(() =>
            {
                _vm.AxisXMin = newMinLocal;
                _vm.AxisXMax = newMaxLocal;
            });

            _ = MaybeLoadMoreHistoryAsync(newMinLocal, newMaxLocal);
        }

        /// <summary>
        /// Безопасный polling (не убивает общий polling при ошибке).
        /// </summary>
        public async Task PollOnceSafeAsync(CancellationToken ct, Action<string>? setBottomText = null)
        {
            try
            {
                // При потере связи не трогаем тренды — это один из самых дорогих путей.
                if (!_ctApiService.IsConnectionAvailable)
                    return;

                await _trendGate.WaitAsync(ct);
                try
                {
                    await PollOnceAsync(ct);
                }
                finally
                {
                    _trendGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                setBottomText?.Invoke($"Trend error: {ex.Message}");
            }
        }

        /// <summary>
        /// Основная логика чтения трендов.
        /// Live: ось X pinned к now-window..now, точки режем по окну.
        /// History: ось X НЕ трогаем (ею управляет DevExpress), обновляем WholeRange и (опционально) триммим память.
        /// </summary>
        private async Task PollOnceAsync(CancellationToken ct)
        {
            var (equipName, _, _) = _resolveEquip();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            // смена оборудования -> сброс
            if (!string.Equals(_trendEquipName, equipName, StringComparison.OrdinalIgnoreCase))
            {
                _trendEquipName = equipName;
                _trnNameByItem.Clear();
                _lastUtcByItem.Clear();

                await _ui.InvokeAsync(() => _vm.Points.Clear());

                // возвращаемся в Live по смыслу
                _vm.IsLiveMode = true;
            }

            var model = _getParamModel();

            // TrendItems из атрибутов модели
            var trendItems = GetTrendItemsFromModel(model, "R");
            if (trendItems.Length == 0) trendItems = new[] { "R" };

            var baseItem = trendItems[0];

            // базовая шкала Y
            if (!TryGetBaseYRange(model, baseItem, out var baseMin, out var baseMax))
            {
                baseMin = 0;
                baseMax = 1;
            }

            var endUtc = DateTime.UtcNow;

            foreach (var item in trendItems)
            {
                ct.ThrowIfCancellationRequested();

                // 1) trnName
                if (!_trnNameByItem.TryGetValue(item, out var trnName) || string.IsNullOrWhiteSpace(trnName))
                {
                    trnName = await _equipmentService.GetTrnName(equipName, item);
                    if (string.IsNullOrWhiteSpace(trnName))
                        continue;

                    _trnNameByItem[item] = trnName;
                }

                // 2) окно чтения по lastUtc
                var startUtc = _lastUtcByItem.TryGetValue(item, out var lastUtc)
                    ? lastUtc.AddSeconds(-2)
                    : endUtc.AddMinutes(-Math.Max(1, _vm.WindowMinutes));

                var trn = await _ctApiService.GetTrnData(trnName, startUtc, endUtc);
                if (trn == null || trn.Count == 0)
                    continue;

                // native Y-range для серии
                double nativeMin = baseMin, nativeMax = baseMax;
                if (!item.Equals(baseItem, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetYRangeForItem(model, item, out var aMin, out var aMax))
                    {
                        nativeMin = aMin;
                        nativeMax = aMax;
                    }
                }

                var points = trn
                    .Select(x =>
                    {
                        var raw = x.Value;

                        var plot = item.Equals(baseItem, StringComparison.OrdinalIgnoreCase)
                            ? raw
                            : MapToBase(raw, nativeMin, nativeMax, baseMin, baseMax);

                        return new TrendPoint
                        {
                            Series = item,
                            Time = DateTime.SpecifyKind(x.DateTime, DateTimeKind.Utc).ToLocalTime(),
                            RawValue = raw,
                            Value = plot
                        };
                    })
                    .OrderBy(p => p.Time)
                    .ToList();

                if (points.Count == 0)
                    continue;

                await _ui.InvokeAsync(() =>
                {
                    // добавляем только новые точки для серии
                    var lastAdded = _vm.Points
                        .Where(p => p.Series.Equals(item, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Time)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();

                    foreach (var p in points)
                        if (p.Time > lastAdded)
                            _vm.Points.Add(p);

                    // оси Y (всегда базовая)
                    _vm.AxisYMin = baseMin;
                    _vm.AxisYMax = baseMax;

                    var now = DateTime.Now;

                    if (_vm.IsLiveMode)
                    {
                        NavSuppressEnter();
                        try
                        {
                            var win = Math.Max(1, _vm.WindowMinutes);
                            _vm.AxisXMax = now;
                            _vm.AxisXMin = now.AddMinutes(-win);

                            _vm.AxisXWholeMin = _vm.AxisXMin;
                            _vm.AxisXWholeMax = _vm.AxisXMax;
                        }
                        finally
                        {
                            NavSuppressExit();
                        }

                        // trimming можно оставлять без suppress
                        var minKeep = _vm.AxisXMin;
                        for (int i = _vm.Points.Count - 1; i >= 0; i--)
                            if (_vm.Points[i].Time < minKeep)
                                _vm.Points.RemoveAt(i);
                    }
                    else
                    {
                        // History: VisualRange НЕ трогаем
                        UpdateWholeRangeFromPoints_NoThrow();
                        TrimIfNeeded_NoThrow();
                    }

                    _vm.StatusText = $"Trends={trendItems.Length}, Points={_vm.Points.Count} | {now:HH:mm:ss} | {_getParamCycles()} cycles";
                });

                _lastUtcByItem[item] = trn.Max(x => x.DateTime);
            }
        }

        /// <summary>
        /// Автоподгрузка истории при подходе к левому краю.
        /// </summary>
        private async Task MaybeLoadMoreHistoryAsync(DateTime visibleMinLocal, DateTime visibleMaxLocal)
        {
            try
            {
                await _trendGate.WaitAsync(CancellationToken.None);
                try
                {
                    DateTime loadedMinLocal = DateTime.MinValue;
                    DateTime loadedMaxLocal = DateTime.MinValue;

                    await _ui.InvokeAsync(() =>
                    {
                        if (_vm.Points.Count == 0)
                            return;

                        loadedMinLocal = _vm.Points.Min(p => p.Time);
                        loadedMaxLocal = _vm.Points.Max(p => p.Time);
                    });

                    if (loadedMinLocal == DateTime.MinValue || loadedMaxLocal == DateTime.MinValue)
                        return;

                    var visSpan = visibleMaxLocal - visibleMinLocal;
                    if (visSpan <= TimeSpan.Zero)
                        visSpan = TimeSpan.FromMinutes(Math.Max(1, _vm.WindowMinutes));

                    // если пользователь близко к левому краю загруженных данных — добираем ещё кусок
                    var threshold = loadedMinLocal + TimeSpan.FromTicks((long)(visSpan.Ticks * 0.15));
                    if (visibleMinLocal > threshold)
                        return;

                    var toUtc = loadedMinLocal.ToUniversalTime();
                    var fromUtc = toUtc.AddMinutes(-HistoryChunkMinutes);

                    await LoadHistoryWindowAsync(fromUtc, toUtc, CancellationToken.None);

                    await _ui.InvokeAsync(UpdateWholeRangeFromPoints_NoThrow);
                }
                finally
                {
                    _trendGate.Release();
                }
            }
            catch
            {
                // history load не должен ломать UI
            }
        }

        /// <summary>
        /// Добирает историю по всем сериям и мержит в Points.
        /// ВАЖНО: НЕ меняет AxisXMin/AxisXMax (это VisualRange пользователя).
        /// </summary>
        private async Task LoadHistoryWindowAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            var (equipName, _, _) = _resolveEquip();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            // защита от смешивания если оборудование уже переключили
            if (!string.Equals(_trendEquipName, equipName, StringComparison.OrdinalIgnoreCase))
                return;

            var model = _getParamModel();

            var trendItems = GetTrendItemsFromModel(model, "R");
            if (trendItems.Length == 0)
                trendItems = new[] { "R" };

            var baseItem = trendItems[0];
            if (!TryGetBaseYRange(model, baseItem, out var baseMin, out var baseMax))
            {
                baseMin = 0;
                baseMax = 1;
            }

            var newPoints = new List<TrendPoint>(capacity: 512);

            foreach (var item in trendItems)
            {
                ct.ThrowIfCancellationRequested();

                if (!_trnNameByItem.TryGetValue(item, out var trnName) || string.IsNullOrWhiteSpace(trnName))
                {
                    trnName = await _equipmentService.GetTrnName(equipName, item);
                    if (string.IsNullOrWhiteSpace(trnName))
                        continue;

                    _trnNameByItem[item] = trnName;
                }

                var trn = await _ctApiService.GetTrnData(trnName, fromUtc, toUtc);
                if (trn == null || trn.Count == 0)
                    continue;

                double nativeMin = baseMin, nativeMax = baseMax;
                if (!item.Equals(baseItem, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetYRangeForItem(model, item, out var aMin, out var aMax))
                    {
                        nativeMin = aMin;
                        nativeMax = aMax;
                    }
                }

                foreach (var x in trn)
                {
                    var raw = x.Value;
                    var plot = item.Equals(baseItem, StringComparison.OrdinalIgnoreCase)
                        ? raw
                        : MapToBase(raw, nativeMin, nativeMax, baseMin, baseMax);

                    newPoints.Add(new TrendPoint
                    {
                        Series = item,
                        Time = DateTime.SpecifyKind(x.DateTime, DateTimeKind.Utc).ToLocalTime(),
                        RawValue = raw,
                        Value = plot,
                    });
                }
            }

            if (newPoints.Count == 0)
                return;

            await _ui.InvokeAsync(() =>
            {
                // Дедуп по (Series, Time)
                var merged = _vm.Points
                    .Concat(newPoints)
                    .GroupBy(p => (series: (p.Series ?? "").Trim().ToUpperInvariant(), time: p.Time))
                    .Select(g => g.First())
                    .OrderBy(p => p.Time)
                    .ThenBy(p => p.Series, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _vm.Points.Clear();
                foreach (var p in merged)
                    _vm.Points.Add(p);

                UpdateWholeRangeFromPoints_NoThrow();
                TrimIfNeeded_NoThrow();
            });
        }

        /// <summary>
        /// WholeRange = весь загруженный диапазон.
        /// Вызывать на UI thread.
        /// </summary>
        private void UpdateWholeRangeFromPoints_NoThrow()
        {
            if (_vm.Points.Count == 0)
                return;

            _vm.AxisXWholeMin = _vm.Points.Min(p => p.Time);
            _vm.AxisXWholeMax = _vm.Points.Max(p => p.Time);
        }

        /// <summary>
        /// Safety trim в history.
        /// Вызывать на UI thread.
        /// </summary>
        private void TrimIfNeeded_NoThrow()
        {
            if (HistoryKeepHours <= 0)
                return;

            if (_vm.Points.Count == 0)
                return;

            var newest = _vm.Points.Max(p => p.Time);
            var cut = newest.AddHours(-HistoryKeepHours);

            for (int i = _vm.Points.Count - 1; i >= 0; i--)
                if (_vm.Points[i].Time < cut)
                    _vm.Points.RemoveAt(i);

            UpdateWholeRangeFromPoints_NoThrow();
        }

        #region helpers

        private static string[] GetTrendItemsFromModel(object? model, params string[] fallback)
        {
            var rawModel = ParamModelHelper.Unwrap(model);
            if (rawModel == null)
                return fallback;

            var items = rawModel.GetType()
                .GetCustomAttributes(typeof(TrendItemAttribute), inherit: true)
                .OfType<TrendItemAttribute>()
                .Select(a => a.Item)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return items.Length > 0 ? items : fallback;
        }

        private static bool TryGetYRangeForItem(object? model, string item, out double yMin, out double yMax)
        {
            yMin = 0;
            yMax = 1;

            var rawModel = ParamModelHelper.Unwrap(model);
            if (rawModel == null)
                return false;

            var a = rawModel.GetType()
                .GetCustomAttributes(typeof(TrendItemAttribute), true)
                .OfType<TrendItemAttribute>()
                .FirstOrDefault(x => string.Equals(x.Item, item, StringComparison.OrdinalIgnoreCase));

            if (a == null || !a.HasYRange)
                return false;

            yMin = Math.Min(a.YMin, a.YMax);
            yMax = Math.Max(a.YMin, a.YMax);
            return true;
        }

        private static bool TryGetModelScaleMinMax(object? model, out double scaleLo, out double scaleHi)
        {
            scaleLo = 0;
            scaleHi = 1;

            var rawModel = ParamModelHelper.Unwrap(model);
            if (rawModel == null)
                return false;

            var t = rawModel.GetType();
            var pMinR = t.GetProperty("MinR");
            var pMaxR = t.GetProperty("MaxR");
            if (pMinR == null || pMaxR == null)
                return false;

            var vMin = pMinR.GetValue(rawModel);
            var vMax = pMaxR.GetValue(rawModel);
            if (vMin == null || vMax == null)
                return false;

            var a = Convert.ToDouble(vMin, System.Globalization.CultureInfo.InvariantCulture);
            var b = Convert.ToDouble(vMax, System.Globalization.CultureInfo.InvariantCulture);

            scaleLo = Math.Min(a, b);
            scaleHi = Math.Max(a, b);
            return true;
        }

        //private static string[] GetTrendItemsFromModel(object? model, params string[] fallback)
        //{
        //    if (model == null) return fallback;

        //    var items = model.GetType()
        //        .GetCustomAttributes(typeof(TrendItemAttribute), inherit: true)
        //        .OfType<TrendItemAttribute>()
        //        .Select(a => a.Item)
        //        .Where(s => !string.IsNullOrWhiteSpace(s))
        //        .Select(s => s.Trim())
        //        .Distinct(StringComparer.OrdinalIgnoreCase)
        //        .ToArray();

        //    return items.Length > 0 ? items : fallback;
        //}

        //private static bool TryGetYRangeForItem(object? model, string item, out double yMin, out double yMax)
        //{
        //    yMin = 0; yMax = 1;
        //    if (model == null) return false;

        //    var a = model.GetType()
        //        .GetCustomAttributes(typeof(TrendItemAttribute), true)
        //        .OfType<TrendItemAttribute>()
        //        .FirstOrDefault(x => string.Equals(x.Item, item, StringComparison.OrdinalIgnoreCase));

        //    if (a == null || !a.HasYRange) return false;

        //    yMin = Math.Min(a.YMin, a.YMax);
        //    yMax = Math.Max(a.YMin, a.YMax);
        //    return true;
        //}

        //private static bool TryGetModelScaleMinMax(object? model, out double scaleLo, out double scaleHi)
        //{
        //    scaleLo = 0; scaleHi = 1;
        //    if (model == null) return false;

        //    var t = model.GetType();
        //    var pMinR = t.GetProperty("MinR");
        //    var pMaxR = t.GetProperty("MaxR");
        //    if (pMinR == null || pMaxR == null) return false;

        //    var vMin = pMinR.GetValue(model);
        //    var vMax = pMaxR.GetValue(model);
        //    if (vMin == null || vMax == null) return false;

        //    var a = Convert.ToDouble(vMin, CultureInfo.InvariantCulture);
        //    var b = Convert.ToDouble(vMax, CultureInfo.InvariantCulture);

        //    scaleLo = Math.Min(a, b);
        //    scaleHi = Math.Max(a, b);
        //    return true;
        //}

        private static bool TryGetBaseYRange(object? model, string baseItem, out double baseMin, out double baseMax)
        {
            if (TryGetYRangeForItem(model, baseItem, out baseMin, out baseMax))
                return true;

            if (TryGetModelScaleMinMax(model, out baseMin, out baseMax))
                return true;

            baseMin = 0; baseMax = 1;
            return false;
        }

        private static double MapToBase(double raw, double fromMin, double fromMax, double baseMin, double baseMax)
        {
            var fromSpan = fromMax - fromMin;
            if (Math.Abs(fromSpan) < 1e-12)
                return baseMin;

            var t = (raw - fromMin) / fromSpan;
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            return baseMin + t * (baseMax - baseMin);
        }

        private static int NormalizeWindowMinutes(double minutes)
        {
            if (double.IsNaN(minutes) || double.IsInfinity(minutes))
                return 30;

            var m = Math.Max(1, minutes);

            // выбираем ближайшее к пресетам (чтобы ComboBox выбрал элемент)
            int best = WindowPresets[0];
            double bestDiff = Math.Abs(best - m);

            for (int i = 1; i < WindowPresets.Length; i++)
            {
                var diff = Math.Abs(WindowPresets[i] - m);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = WindowPresets[i];
                }
            }

            return best;
        }

        private static bool ShouldSnapToLive(DateTime visibleMaxLocal, TimeSpan span)
        {
            // Чем меньше масштаб — тем меньше допуск.
            // 2% от окна, но минимум 5 сек и максимум 30 сек.
            var tol = TimeSpan.FromSeconds(Math.Clamp(span.TotalSeconds * 0.02, 5, 30));
            return (DateTime.Now - visibleMaxLocal) <= tol;
        }

        private void SuppressUserRangeChangedFor(TimeSpan duration)
        {
            Interlocked.Increment(ref _suppressUserRangeChanged);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(duration); } catch { }
                Interlocked.Decrement(ref _suppressUserRangeChanged);
            });
        }

        private bool IsAtRightEdge(DateTime visibleMaxLocal)
        {
            // WholeRangeMax = самый правый край загруженных точек
            var wholeMax = _vm.AxisXWholeMax;

            // если по какой-то причине default — сравним с now
            if (wholeMax == default)
                wholeMax = DateTime.Now;

            return visibleMaxLocal >= wholeMax - RightEdgeTolerance;
        }

        #endregion
    }
}
