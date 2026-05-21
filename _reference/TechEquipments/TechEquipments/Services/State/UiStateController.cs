using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TechEquipments.ViewModels;

namespace TechEquipments
{
    /// <summary>
    /// Контролер сохранения/восстановления состояния UI:
    /// - Restore из user-state.json
    /// - Startup из ExternalTag (с приоритетом)
    /// - Debounce Save
    /// - Флаги StartupExternalTag для применения фильтров после загрузки Equipments
    /// </summary>
    public sealed class UiStateController
    {
        private readonly IUserStateService _stateService;
        private readonly IEquipmentService _equipmentService;
        private readonly MainViewModel _vm;
        private readonly Dispatcher _dispatcher;

        private readonly Action<string> _setEquipName;
        private readonly Action<DateTime> _setDbDate;
        private readonly Action<string> _setSelectedStation;
        private readonly Action<EquipTypeGroup> _setSelectedTypeFilter;
        private readonly Action<int> _setSelectedMainTabIndex;
        private readonly Func<Dictionary<string, string>> _exportRememberedEquipmentsByFilter;
        private readonly Action<Dictionary<string, string>?> _importRememberedEquipmentsByFilter;

        private readonly DispatcherTimer _saveTimer;

        private bool _isRestoringState;

        // Startup info (нужно MainWindow после загрузки Equipments)
        private bool _startupUsedExternalTag;
        private string _startupExternalTag = "";

        public UiStateController(
            IUserStateService stateService,
            IEquipmentService equipmentService,
            MainViewModel vm,
            Dispatcher dispatcher,
            Action<string> setEquipName,
            Action<DateTime> setDbDate,
            Action<string> setSelectedStation,
            Action<EquipTypeGroup> setSelectedTypeFilter,
            Action<int> setSelectedMainTabIndex,
            Func<Dictionary<string, string>> exportRememberedEquipmentsByFilter,
            Action<Dictionary<string, string>?> importRememberedEquipmentsByFilter)
        {
            _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _setEquipName = setEquipName ?? throw new ArgumentNullException(nameof(setEquipName));
            _setDbDate = setDbDate ?? throw new ArgumentNullException(nameof(setDbDate));
            _setSelectedStation = setSelectedStation ?? throw new ArgumentNullException(nameof(setSelectedStation));
            _setSelectedTypeFilter = setSelectedTypeFilter ?? throw new ArgumentNullException(nameof(setSelectedTypeFilter));
            _setSelectedMainTabIndex = setSelectedMainTabIndex ?? throw new ArgumentNullException(nameof(setSelectedMainTabIndex));
            _exportRememberedEquipmentsByFilter = exportRememberedEquipmentsByFilter ?? throw new ArgumentNullException(nameof(exportRememberedEquipmentsByFilter));
            _importRememberedEquipmentsByFilter = importRememberedEquipmentsByFilter ?? throw new ArgumentNullException(nameof(importRememberedEquipmentsByFilter));

            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };

            _saveTimer.Tick += async (_, __) =>
            {
                _saveTimer.Stop();
                try
                {
                    await SaveAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            };
        }

        public bool IsRestoringState => _isRestoringState;

        public bool StartupUsedExternalTag => _startupUsedExternalTag;
        public string StartupExternalTag => _startupExternalTag;

        /// <summary>
        /// Планируем сохранение (debounce).
        /// Вызывай из setter’ов MainWindow вместо ScheduleStateSave().
        /// </summary>
        public void ScheduleSave()
        {
            if (_isRestoringState)
                return;

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// Восстановление из ExternalTag (приоритет над user-state.json).
        /// </summary>
        public async Task<bool> TryApplyStartupStateFromExternalTagAsync()
        {
            try
            {
                var ext = await _equipmentService.GetExternalTagAsync(CancellationToken.None);

                if (string.IsNullOrWhiteSpace(ext) ||
                    ext.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    _startupUsedExternalTag = false;
                    _startupExternalTag = "";
                    return false;
                }

                _startupUsedExternalTag = true;
                _startupExternalTag = ext.Trim();

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _equipmentService.SetExternalTagAsync("", cts.Token);
                }
                catch
                {
                    // ignore
                }

                _isRestoringState = true;
                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        _setEquipName(_startupExternalTag);
                        _setSelectedMainTabIndex((int)MainTabKind.Param);
                        _setDbDate(DateTime.Today);
                        _setSelectedStation("All");
                        _setSelectedTypeFilter(EquipTypeGroup.All);
                    }, DispatcherPriority.Background);
                }
                finally
                {
                    _isRestoringState = false;
                }

                return true;
            }
            catch
            {
                _startupUsedExternalTag = false;
                _startupExternalTag = "";
                return false;
            }
        }

        /// <summary>
        /// Восстановление состояния из user-state.json.
        /// </summary>
        public async Task RestoreStateAsync()
        {
            _isRestoringState = true;
            try
            {
                var state = await _stateService.LoadAsync();
                if (state == null)
                    return;

                await _dispatcher.InvokeAsync(() =>
                {
                    _importRememberedEquipmentsByFilter(state.LastEquipmentsByFilter);

                    _setEquipName(state.LastEquipName ?? "");
                    //_setDbDate(state.DbDate.Date); // не восстанавливаем дату для истории

                    _setSelectedStation(state.SelectedStation ?? "All");
                    _setSelectedTypeFilter(state.SelectedTypeFilter);

                    _setSelectedMainTabIndex((int)state.SelectedTab);
                }, DispatcherPriority.Background);
            }
            finally
            {
                _isRestoringState = false;
            }
        }

        /// <summary>
        /// Сохранение состояния сразу (обычно вызывается таймером).
        /// ВАЖНО:
        /// не создаём UserState с нуля, иначе можно потерять поля,
        /// которыми управляют другие части приложения
        /// (например, QrCameraIndex).
        /// </summary>
        public async Task SaveAsync()
        {
            if (_isRestoringState)
                return;

            // Загружаем уже существующее состояние, чтобы сохранить "чужие" поля,
            // например QrCameraIndex, который пишет QR scanner service.
            var state = await _stateService.LoadAsync() ?? new UserState();

            state.LastEquipName = (_vm.EquipmentList.EquipName ?? "").Trim();
            state.DbDate = _vm.Database.DbDate.Date;
            state.SelectedTab = _vm.SelectedMainTab;
            state.SelectedStation = (_vm.EquipmentList.SelectedStation ?? "All").Trim();
            state.SelectedTypeFilter = _vm.EquipmentList.SelectedTypeFilter;
            state.LastEquipmentsByFilter = _exportRememberedEquipmentsByFilter();

            await _stateService.SaveAsync(state);
        }

    }
}
