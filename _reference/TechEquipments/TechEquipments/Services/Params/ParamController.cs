using CtApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TechEquipments.ViewModels;

namespace TechEquipments
{
    /// <summary>
    /// Вся логіка Param-polling:
    /// - цикл раз в 5 секунд
    /// - PollParamOnceSafeAsync (gate, pause-after-write, editing protection)
    /// - PollParamOnceAsync (определение типа и чтение модели)
    /// - ApplyParamModelToUi (кеш props + обновление значений без лагов)
    ///
    /// Працює напряму з MainViewModel, без IParamHost.
    /// </summary>
    public sealed class ParamController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly MainViewModel _vm;
        private readonly Dispatcher _dispatcher;
        private readonly ICtApiService _ctApiService;
        private readonly SemaphoreSlim _paramRwGate;

        private readonly Func<bool> _getTrendIsChartVisible;
        private readonly Func<bool> _getIsEditingField;
        private readonly Func<(string equipName, string equipType, string equipDescription)> _resolveSelectedEquipForParam;
        private readonly Action<EquipTypeGroup> _resetAreaIfTypeGroupChanged;
        private readonly Func<CancellationToken, Task> _refreshActiveParamSectionAsync;
        private readonly Func<CancellationToken, Task> _pollTrendOnceSafeAsync;
        private readonly Action<string, ParamLoadState> _notifyMainParamLoaded;

        // защита от гонок Start/Stop (SelectionChanged + TabChanged могут дернуть одновременно)
        private readonly object _sync = new();

        // ключ "что именно сейчас поллим" (equip + type)
        // если ключ поменялся -> перезапускаем polling
        private string _pollKey = "";

        private CancellationTokenSource? _cts;

        // --- UI apply cache ---
        private Type? _currentParamModelType;
        private readonly Dictionary<Type, PropertyInfo[]> _uiPropsCache = new();
        private readonly Dictionary<string, int> _rowIndexByName = new(StringComparer.Ordinal);

        private DateTime _nextSecurityTitleReadUtc = DateTime.MinValue;

        public ParamController(
            IEquipmentService equipmentService,
            MainViewModel vm,
            Dispatcher dispatcher,
            ICtApiService ctApiService,
            SemaphoreSlim paramRwGate,
            Func<bool> getTrendIsChartVisible,
            Func<bool> getIsEditingField,
            Func<(string equipName, string equipType, string equipDescription)> resolveSelectedEquipForParam,
            Action<EquipTypeGroup> resetAreaIfTypeGroupChanged,
            Func<CancellationToken, Task> refreshActiveParamSectionAsync,
            Func<CancellationToken, Task> pollTrendOnceSafeAsync,
            Action<string, ParamLoadState> notifyMainParamLoaded)
        {
            _equipmentService = equipmentService;
            _vm = vm;
            _dispatcher = dispatcher;
            _ctApiService = ctApiService;
            _paramRwGate = paramRwGate;
            _getTrendIsChartVisible = getTrendIsChartVisible;
            _getIsEditingField = getIsEditingField;
            _resolveSelectedEquipForParam = resolveSelectedEquipForParam;
            _resetAreaIfTypeGroupChanged = resetAreaIfTypeGroupChanged;
            _refreshActiveParamSectionAsync = refreshActiveParamSectionAsync;
            _pollTrendOnceSafeAsync = pollTrendOnceSafeAsync;
            _notifyMainParamLoaded = notifyMainParamLoaded;
        }

        public void Start()
        {
            // Polling имеет смысл только на вкладке Param
            if (_vm.SelectedMainTab != MainTabKind.Param)
                return;

            var newKey = BuildPollKey();

            lock (_sync)
            {
                // 1) если polling еще не запущен — запускаем как обычно
                if (_cts == null)
                {
                    StartInternal_NoLock(newKey);
                    return;
                }

                // 2) если polling запущен и ключ тот же — ничего не делаем
                if (string.Equals(_pollKey, newKey, StringComparison.OrdinalIgnoreCase))
                    return;

                // 3) ключ поменялся => выбрали другое оборудование: restart + сброс статуса/счетчика
                StopInternal_NoLock();
                StartInternal_NoLock(newKey);
            }
        }

        /// <summary>
        /// Старт polling (предполагается, что lock уже взят).
        /// </summary>
        private void StartInternal_NoLock(string pollKey)
        {
            _pollKey = pollKey;

            _vm.Param.ParamReadCycles = 0;
            _vm.Shell.ParamStatusText = "Param: starting...";

            _currentParamModelType = null;
            _rowIndexByName.Clear();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        //await RefreshWindowTitleFromSecurityAsync(ct);

                        if (_vm.SelectedMainTab != MainTabKind.Param)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), ct);
                            continue;
                        }

                        await PollParamOnceSafeAsync(ct);
                        await _refreshActiveParamSectionAsync(ct);

                        if (_getTrendIsChartVisible())
                            await _pollTrendOnceSafeAsync(ct);

                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }, ct);
        }

        public void Stop()
        {
            lock (_sync)
            {
                StopInternal_NoLock();
                _pollKey = "";
            }
        }

        /// <summary>
        /// Стоп polling (предполагается, что lock уже взят).
        /// </summary>
        private void StopInternal_NoLock()
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
        }

        private async Task PollParamOnceSafeAsync(CancellationToken ct)
        {
            try
            {
                if (!_ctApiService.IsConnectionAvailable)
                {
                    _vm.Shell.ParamStatusText = "CtApi disconnected";
                    return;
                }

                if (DateTime.UtcNow < _vm.Param.ParamReadResumeAtUtc)
                    return;

                if (_getIsEditingField())
                    return;

                await _paramRwGate.WaitAsync(ct);
                try
                {
                    await PollParamOnceAsync(ct);
                }
                finally
                {
                    _paramRwGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _vm.Shell.BottomText = $"Param read error: {ex.Message}";

                var (equipName, _, _) = _resolveSelectedEquipForParam();
                _notifyMainParamLoaded((equipName ?? "").Trim(), ParamLoadState.Error);
            }
        }

        private async Task PollParamOnceAsync(CancellationToken ct)
        {
            var (equipName, equipType, _) = _resolveSelectedEquipForParam();

            equipName = (equipName ?? "").Trim();
            equipType = (equipType ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipName))
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    _vm.Shell.ParamStatusText = "Param: select equipment";
                    _vm.Param.ParamItems.Clear();
                    _currentParamModelType = null;
                    _vm.Param.CurrentParamModel = null;

                    _notifyMainParamLoaded("", ParamLoadState.Unavailable);
                });
                return;
            }

            var typeGroup = EquipTypeRegistry.GetGroup(equipType);

            object? rawParam = typeGroup switch
            {
                EquipTypeGroup.AI => await _equipmentService.ReadEquipParamsAsync<AIParam>(equipName, ct),
                EquipTypeGroup.DI => await _equipmentService.ReadEquipParamsAsync<DIParam>(equipName, ct),
                EquipTypeGroup.DO => await _equipmentService.ReadEquipParamsAsync<DOParam>(equipName, ct),
                EquipTypeGroup.Atv => await _equipmentService.ReadEquipParamsAsync<AtvParam>(equipName, ct),
                EquipTypeGroup.Motor => await _equipmentService.ReadEquipParamsAsync<MotorParam>(equipName, ct),
                EquipTypeGroup.VGA_EL => await _equipmentService.ReadEquipParamsAsync<VGA_ElParam>(equipName, ct),
                EquipTypeGroup.VGA => await _equipmentService.ReadEquipParamsAsync<VGAParam>(equipName, ct),
                EquipTypeGroup.VGD => await _equipmentService.ReadEquipParamsAsync<VGDParam>(equipName, ct),
                _ => null
            };

            object? model = ParamModelFactory.Create(typeGroup, rawParam);

            ct.ThrowIfCancellationRequested();

            await _dispatcher.InvokeAsync(() =>
            {
                _resetAreaIfTypeGroupChanged(typeGroup);
                _vm.Param.SuppressParamWritesFromPolling = true;

                try
                {
                    if (model == null)
                    {
                        _vm.Shell.ParamStatusText = "Updating ...";
                        _vm.Param.ParamItems.Clear();
                        _currentParamModelType = null;
                        _vm.Param.CurrentParamModel = null;

                        _notifyMainParamLoaded(equipName, ParamLoadState.Unavailable);
                        return;
                    }

                    ApplyParamModelToUi(model);

                    _vm.Param.ParamReadCycles++;
                    _vm.Shell.ParamStatusText = $"Last update: {DateTime.Now:HH:mm:ss} | {_vm.Param.ParamReadCycles} cycles";

                    _notifyMainParamLoaded(equipName, ParamLoadState.Ready);
                }
                finally
                {
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        _vm.Param.SuppressParamWritesFromPolling = false;
                    }), DispatcherPriority.ContextIdle);
                }
            });
        }

        private void ApplyParamModelToUi(object model)
        {
            _vm.Param.CurrentParamModel = model;

            var rawParam = ParamModelHelper.Unwrap(model);
            if (rawParam == null)
            {
                _vm.Param.ParamItems.Clear();
                _currentParamModelType = null;
                return;
            }

            var rawParamType = rawParam.GetType();

            if (!_uiPropsCache.TryGetValue(rawParamType, out var props))
            {
                props = rawParamType
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .Where(p =>
                        !p.Name.Equals("Unit", StringComparison.OrdinalIgnoreCase) &&
                        !p.Name.Equals("Chanel", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.MetadataToken)
                    .ToArray();

                _uiPropsCache[rawParamType] = props;
            }

            if (_currentParamModelType != rawParamType)
            {
                _vm.Param.ParamItems.Clear();
                _rowIndexByName.Clear();

                for (int i = 0; i < props.Length; i++)
                {
                    var p = props[i];

                    _vm.Param.ParamItems.Add(new ParamItem
                    {
                        Name = p.Name,
                        Value = p.GetValue(rawParam)
                    });

                    _rowIndexByName[p.Name] = i;
                }

                _currentParamModelType = rawParamType;
                return;
            }

            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];

                if (_rowIndexByName.TryGetValue(p.Name, out var rowIndex) &&
                    rowIndex >= 0 && rowIndex < _vm.Param.ParamItems.Count)
                {
                    _vm.Param.ParamItems[rowIndex].Value = p.GetValue(rawParam);
                }
            }
        }

        /// <summary>
        /// Формируем ключ polling (что именно сейчас поллим).
        /// Если изменится — считаем что выбрали другое оборудование.
        /// </summary>
        private string BuildPollKey()
        {
            var (equipName, equipType, _) = _resolveSelectedEquipForParam();

            equipName = (equipName ?? "").Trim();
            equipType = (equipType ?? "").Trim();

            return $"{equipName}|{equipType}";
        }

        private async Task RefreshWindowTitleFromSecurityAsync(CancellationToken ct)
        {
            var nowUtc = DateTime.UtcNow;
            if (nowUtc < _nextSecurityTitleReadUtc)
                return;

            _nextSecurityTitleReadUtc = nowUtc.AddSeconds(2);

            try
            {
                var fullName = (await _ctApiService.UserInfoAsync(2)).Trim();

                await _dispatcher.InvokeAsync(() =>
                {
                    _vm.Shell.CurrentCtUserName = fullName;
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // молча игнорируем transient ошибки чтения security
            }
        }
    }
}
