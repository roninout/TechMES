using CtApi;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Grid;
using DevExpress.Xpf.PdfViewer;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TechEquipments.ViewModels;
using TechEquipments.Views.Settings;

namespace TechEquipments
{
    /// <summary>
    /// Главное окно приложения:
    /// - Левая панель: список оборудования + фильтры Station/Type + посимвольный поиск.
    /// - Правая панель: вкладки SOE / Operation actions / Alarm history.
    /// - Нижняя панель прогресса: используется для загрузки списка оборудования и DB (индетерминантно).
    /// - Overlay: используется для загрузки SOE (тренды).
    /// </summary>
    public partial class MainWindow : ThemedWindow, INotifyPropertyChanged
    {
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;
        private readonly IConfiguration _configService;
        private readonly IEquipInfoService _equipInfoService;
        private readonly IAppRuntimeContext _appRuntime;

        private readonly ParamController _paramController;
        private readonly ParamWriteController _paramWriteController;
        private readonly ParamTrendController _trendController;
        private readonly ParamRefsController _paramRefsController;
        private readonly DbController _dbController;
        private readonly QrController _qrController;
        private readonly SoeController _soeController;
        private readonly UiStateController _uiStateController;
        private readonly InfoController _infoController;
        private readonly EquipmentListController _equipmentListController;
        private readonly MessageController _messageController;

        public MainViewModel Vm { get; }
        public ParamTrendVm Trend { get; }
        private EquipmentListViewModel EquipVm => Vm.EquipmentList;

        /// <summary>Строки SOE (вкладка SOE).</summary>
        public ObservableCollection<EquipmentSOEDto> equipmentSOEDtos { get; } = new();

        private CancellationTokenSource? _equipListCts; // CTS для отмены загрузки списка оборудования.
        private CancellationTokenSource? _stationHealthCts;

        #region Fields

        #region Left pane: search + filters + selection

        private bool _suppressStartupTreeSelectionSideEffects;

        public ICollectionView EquipmentsView => _equipmentListController.EquipmentsView;

        public Array TypeFilters { get; } = Enum.GetValues(typeof(EquipTypeGroup));

        #endregion

        #region Tab / date bridge

        public int SelectedMainTabIndex
        {
            get => Vm.SelectedMainTabIndex;
            set
            {
                if (Vm.SelectedMainTabIndex == value) return;

                Vm.SelectedMainTabIndex = value;
                OnPropertyChanged();

                // selected tab как read-only helper ещё используется в code-behind
                OnPropertyChanged(nameof(SelectedMainTab));

                // ВАЖНО: во время восстановления состояния никаких автодействий
                if (_uiStateController.IsRestoringState) return;

                _dbController.CancelCurrentLoad();
                _uiStateController.ScheduleSave();

                _ = OnTabActivatedLikeSearchAsync(force: true);
            }
        }

        public DateTime DbDate
        {
            get => Vm.Database.DbDate;
            set
            {
                if (Vm.Database.DbDate.Date == value.Date) return;
                Vm.Database.DbDate = value.Date;
                OnPropertyChanged();

                _dbController.ScheduleReload();
                _uiStateController.ScheduleSave();
            }
        }

        /// <summary>
        /// Оставляем как helper для code-behind.
        /// </summary>
        public MainTabKind SelectedMainTab => Vm.SelectedMainTab;

        #endregion   

        #region Left pane toggle state

        private bool _layoutReady;
        private GridLength _leftSavedWidth = new(260);

        #endregion

        #region Params

        // Что именно сейчас ждём
        private string? _pendingParamOverlayEquipName;
        private ParamSettingsPage _pendingParamOverlayPage = ParamSettingsPage.None;
        private bool _pendingParamOverlayNeedsMainModel;

        // Последний завершённый статус основной Param-модели
        private string? _lastMainLoadedEquipName;
        private ParamLoadState _lastMainLoadedState = ParamLoadState.Waiting;

        // Последний завершённый статус секции settings
        private string? _lastSectionLoadedEquipName;
        private ParamSettingsPage _lastSectionLoadedPage = ParamSettingsPage.None;
        private ParamLoadState _lastSectionLoadedState = ParamLoadState.Waiting;

        private readonly SemaphoreSlim _paramRwGate = new(1, 1); // Общий “замок” на чтение/запись Param (чтение и запись не пересекаются)

        // ===== Param editing (anti-overwrite during typing) =====

        private int _isEditingField; // 0/1 флаг (Interlocked/Volatile, чтобы безопасно читать из background polling)

        // Быстрая проверка из polling
        private bool IsEditingField => Volatile.Read(ref _isEditingField) == 1;

        private static bool IsFinalParamLoadState(ParamLoadState state) => state is ParamLoadState.Ready or ParamLoadState.Unavailable or ParamLoadState.Error;

        #endregion

        #region Security

        private bool _isLogoLoginToggleInProgress;

        #endregion

        #region Version
        public string AppVersionText => string.IsNullOrWhiteSpace(_appRuntime.AppVersion) ? "" : $"v{_appRuntime.AppVersion}";
        #endregion

        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IEquipInfoService equipInfoService, IMessageService messageService, IUserStateService stateService, ICtApiService ctApiService, IConfiguration config, IQrCodeService qrCodeService, IQrScannerService qrScannerService, MainViewModel vm, IAppRuntimeContext appRuntime)
        {
            InitializeComponent();

            Vm = vm;

            // ===== core services =====
            _equipmentService = equipmentService;
            _equipInfoService = equipInfoService;
            _ctApiService = ctApiService;
            _configService = config;
            _appRuntime = appRuntime;

            // ===== core VM/state =====
            Trend = new ParamTrendVm();
            Trend.AutoLive = _configService.GetValue("Trend:AutoLive", true);

            Vm.Shell.IsCtApiConnected = _ctApiService.IsConnectionAvailable;
            Vm.Shell.UseParamAreaOverlay = _configService.GetValue("Global:Overlay", true);
            Vm.Shell.CurrentCtUserName = _appRuntime.DeviceName;

            Vm.Message.ShowDeleteButton = _configService.GetValue("Messages:ShowDeleteButton", false);
            Vm.Message.RefreshEnabled = _configService.GetValue("Messages:RefreshEnabled", true);
            Vm.Message.RefreshPeriodSeconds = _configService.GetValue("Messages:RefreshPeriodSeconds", 30);
            Vm.Message.MarkAsViewedDelaySeconds = _configService.GetValue("Messages:MarkAsViewedDelaySeconds", 3);

            var showOnlyActiveByDefault = _configService.GetValue("Messages:ShowOnlyActiveByDefault", true);
            Vm.Message.ShowAllMessages = !showOnlyActiveByDefault;

            // ===== controllers =====
            _equipmentListController = new EquipmentListController(Vm, Dispatcher, () => EquipmentsTree);

            _dbController = new DbController(dbService, Vm, Dispatcher);

            _infoController = new InfoController(
                _equipInfoService,
                Vm.Info,
                Vm.EquipmentList,
                this,
                qrScannerService);

            _messageController = new MessageController(
                messageService,
                Vm.Message,
                Dispatcher,
                this,
                () => Vm.Shell.CurrentCtUserName);

            _qrController = new QrController(
                _equipmentService,
                qrCodeService,
                qrScannerService,
                Vm,
                this,
                text => EquipVm.EquipName = text,
                station => EquipVm.SelectedStation = station,
                type => EquipVm.SelectedTypeFilter = type,
                tabIndex => SelectedMainTabIndex = tabIndex,
                _equipmentListController.DoIncrementalSearch,
                StartParamPolling,
                NotifyParamQrUiChanged);

            _soeController = new SoeController(
                _equipmentService,
                Vm.Shell,
                equipmentSOEDtos,
                Dispatcher,
                () => this);

            _uiStateController = new UiStateController(
                stateService,
                _equipmentService,
                Vm,
                Dispatcher,
                equipName => EquipVm.EquipName = equipName,
                dbDate => DbDate = dbDate,
                station => EquipVm.SelectedStation = station,
                type => EquipVm.SelectedTypeFilter = type,
                tabIndex => SelectedMainTabIndex = tabIndex,
                _equipmentListController.ExportRememberedEquipmentsByFilter,
                _equipmentListController.ImportRememberedEquipmentsByFilter);

            _trendController = new ParamTrendController(
                Trend,
                Dispatcher,
                _equipmentService,
                _ctApiService,
                resolveEquip: ResolveSelectedEquipForParam,
                getParamModel: () => Vm.Param.CurrentParamModel,
                getParamCycles: () => Vm.Param.ParamReadCycles);

            _paramWriteController = new ParamWriteController(
                equipmentService: _equipmentService,
                ctApiService: _ctApiService,
                appRuntime: appRuntime,
                requiredWritePrivilege: _configService.GetValue("CtApiSecurity:WritePrivilege", 1),
                requiredWriteArea: _configService.GetValue("CtApiSecurity:WriteArea", 0),
                requiredUserNameContains: _configService["CtApiSecurity:RequiredUserNameContains"] ?? "Tab",
                getSelectedTab: () => SelectedMainTab,
                resolveSelectedEquip: ResolveSelectedEquipForParam,
                resolveEquipNameForWrite: ResolveEquipNameForWrite,
                getSuppressWritesFromPolling: () => Vm.Param.SuppressParamWritesFromPolling,
                getSuppressWritesFromUiRollback: () => Vm.Param.SuppressParamWritesFromUiRollback,
                setSuppressWritesFromUiRollback: v => Vm.Param.SuppressParamWritesFromUiRollback = v,
                paramRwGate: _paramRwGate,
                setParamReadResumeAtUtc: dt => Vm.Param.ParamReadResumeAtUtc = dt,
                setBottomText: txt => Vm.Shell.ParamStatusText = txt,
                getOwnerWindow: () => this,
                endParamFieldEdit: EndParamFieldEdit);

            _paramRefsController = new ParamRefsController(
                _equipmentService,
                _ctApiService,
                _configService,
                Vm,
                Dispatcher,
                _paramRwGate,
                ResolveSelectedEquipForParam,
                _equipmentListController.FilterEquipment,
                _equipmentListController.ApplyFilters,
                _equipmentListController.DoIncrementalSearch,
                ShowParamChart,
                StartParamPolling,
                tabIndex => SelectedMainTabIndex = tabIndex,
                equipName => EquipVm.EquipName = equipName,
                station => EquipVm.SelectedStation = station,
                type => EquipVm.SelectedTypeFilter = type,
                NotifySectionLoadedCore,
                () => Vm.Param.SuppressParamWritesFromPolling = true,
                () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    Vm.Param.SuppressParamWritesFromPolling = false;
                }), DispatcherPriority.ContextIdle));

            _paramController = new ParamController(
                _equipmentService,
                Vm,
                Dispatcher,
                _ctApiService,
                _paramRwGate,
                getTrendIsChartVisible: () => Trend.IsChartVisible,
                getIsEditingField: () => IsEditingField,
                resolveSelectedEquipForParam: ResolveSelectedEquipForParam,
                resetAreaIfTypeGroupChanged: newGroup => _paramRefsController.ResetAreaIfTypeGroupChanged(newGroup),
                refreshActiveParamSectionAsync: ct => _paramRefsController.RefreshActiveParamSectionAsync(ct),
                pollTrendOnceSafeAsync: ct => _trendController.PollOnceSafeAsync(ct, txt => Vm.Shell.BottomText = txt),
                notifyMainParamLoaded: (equipName, state) => NotifyMainParamLoadedCore(equipName, state));

            // ===== view bridge / lifecycle =====
            InitWindowLifecycle();
            InitViewAndBindings();
            InitLoadedHandler();
        }

        #region Startup loading

        /// <summary>
        /// Загружает список оборудования для левой панели + станции.
        /// Показывает внизу детерминантный прогресс.
        /// </summary>
        private async Task LoadEquipmentsListAsync()
        {
            _equipListCts?.Cancel();
            _equipListCts?.Dispose();
            _equipListCts = new CancellationTokenSource();
            var ct = _equipListCts.Token;

            try
            {
                Vm.EquipmentList.IsEquipListLoading = true;

                Vm.EquipmentList.EquipListDone = 0;
                Vm.EquipmentList.EquipListTotal = 0;

                Vm.Shell.BottomText = "Loading equipments...";

                await Dispatcher.Yield(DispatcherPriority.Background);

                var progress = new Progress<(int done, int total)>(p =>
                {
                    Vm.EquipmentList.EquipListDone = p.done;
                    Vm.EquipmentList.EquipListTotal = p.total;
                    Vm.Shell.BottomText = $"Loading equipments: {p.done}/{p.total}";
                });

                var items = await _equipmentService.GetAllEquipmentsAsync(progress, ct);
                await ApplyFavoriteFlagsAsync(items, ct);
                await ApplyInfoIndicatorFlagsAsync(items, ct);

                _suppressStartupTreeSelectionSideEffects = true;
                try
                {
                    _equipmentListController.ReplaceLoadedEquipments(items);
                    RestartStationHealthMonitor();

                    // Если на старте использовали ExternalTag —
                    // сначала выставляем Station/TypeGroup,
                    // а уже потом один раз нормализуем selection.
                    if (_uiStateController.StartupUsedExternalTag && !string.IsNullOrWhiteSpace(_uiStateController.StartupExternalTag))
                    {
                        _qrController.TryApplyStationTypeFiltersFromQr(_uiStateController.StartupExternalTag);
                    }

                    // На startup-load только нормализуем selection,
                    // без автозагрузки Param/Info.
                    RestoreOrSelectEquipmentAfterFilterChanged(suppressAutoActivation: true);

                    // Даём TreeList завершить собственную внутреннюю инициализацию selection.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
                finally
                {
                    _suppressStartupTreeSelectionSideEffects = false;
                }

                Vm.Shell.BottomText = $"Equipments: {_equipmentListController.EquipmentsCount}";
            }
            catch (OperationCanceledException)
            {
                Vm.Shell.BottomText = "Equipments loading cancelled";
            }
            catch (Exception ex)
            {
                Vm.Shell.BottomText = $"Equip list error: {ex.Message}";
                DXMessageBox.Show(this, ex.ToString(), "Equip list error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Vm.EquipmentList.IsEquipListLoading = false;

                // Даём UI шанс корректно перерисовать нижнюю панель.
                await Dispatcher.Yield(DispatcherPriority.Render);
            }
        }

        /// <summary>
        /// Реакция UI на потерю/восстановление связи CtApi.
        /// </summary>
        private void OnCtApiConnectionStateChanged(bool isConnected, string? message)
        {
            void Apply()
            {
                Vm.Shell.IsCtApiConnected = isConnected;

                if (!isConnected)
                {
                    Vm.Shell.CtApiStatusText = string.IsNullOrWhiteSpace(message)
                        ? "CtApi connection lost."
                        : message;
                    MarkAllStationsOffline(true);
                    return;
                }

                Vm.Shell.CtApiStatusText = "";

                // Сообщение о восстановлении связи показываем в обычном BottomText.
                Vm.Shell.BottomText = string.IsNullOrWhiteSpace(message)
                    ? $"CtApi connection restored at {DateTime.Now:HH:mm:ss}"
                    : message;
                RestartStationHealthMonitor();
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)Apply);
                return;
            }

            Apply();
        }

        /// <summary>
        /// Ранняя проверка связи с CtApi на старте окна.
        /// Если связи нет, показываем сообщение и закрываем приложение.
        /// </summary>
        private async Task<bool> EnsureCtApiAvailableAtStartupAsync()
        {
            try
            {
                Vm.Shell.BottomText = "Checking CtApi connection...";
                await Dispatcher.Yield(DispatcherPriority.Background);

                var isConnected = await _ctApiService.IsConnected();
                if (isConnected)
                    return true;

                DXMessageBox.Show(
                    this,
                    "There is no connection to the server.\nThe application will be closed.",
                    "Connection error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Application.Current.Shutdown();
                return false;
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(
                    this,
                    $"Failed to connect to the CtApi server.\n\n{ex.Message}\n\nThe application will be closed.",
                    "Connection error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Application.Current.Shutdown();
                return false;
            }
        }

        private async Task EnsureInfoStorageReadyAsync()
        {
            try
            {
                await _equipInfoService.EnsureDatabaseAndTablesAsync();
                Vm.Info.IsInfoDbConnected = true;
            }
            catch (Exception ex)
            {
                Vm.Info.IsInfoDbConnected = false;
                Vm.Shell.BottomText = $"Info storage init error: {ex.Message}";
            }
        }

        // Lifecycle окна
        private void InitWindowLifecycle()
        {
            _ctApiService.ConnectionStateChanged += OnCtApiConnectionStateChanged;

            Closed += (_, __) =>
            {
                _ctApiService.ConnectionStateChanged -= OnCtApiConnectionStateChanged;
                _messageController.StopBackgroundRefresh();
                StopBackgroundWorkForShutdown();
            };
        }

        // View/init/bindings
        private void InitViewAndBindings()
        {
            SubscribeEquipmentListBridge();

            DataContext = this;

            _equipmentListController.InitEquipmentsView();
            _equipmentListController.InitSearchTimer();

            OnPropertyChanged(nameof(EquipmentsView));
        }

        // Startup Loaded
        private void InitLoadedHandler()
        {
            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

                // CtApi для этого приложения считаем обязательным условием старта.
                // Если связи нет — не запускаем UI в полуживом состоянии.
                //if (!await EnsureCtApiAvailableAtStartupAsync())
                //    return;

                // ExternalTag имеет приоритет над user-state.json.
                var usedExt = await _uiStateController.TryApplyStartupStateFromExternalTagAsync();
                if (!usedExt)
                    await _uiStateController.RestoreStateAsync();

                // Сначала проверяем DB и готовим storage для Info/Favorites.
                await _dbController.CheckDbAsync();
                await EnsureInfoStorageReadyAsync();

                await _messageController.InitializeAsync();
                _messageController.StartBackgroundRefresh();

                // Потом грузим equipment list, чтобы favorites уже могли подтянуться из БД.
                await LoadEquipmentsListAsync();

                await OnTabActivatedLikeSearchAsync(force: true);
            };
        }

        #endregion

        #region Station

        private void RestartStationHealthMonitor()
        {
            StopStationHealthMonitor();

            // Если станций ещё нет — нечего мониторить.
            if (Vm.EquipmentList.Stations.Count <= 1) // только "All"
                return;

            _stationHealthCts = new CancellationTokenSource();
            _ = RunStationHealthMonitorAsync(_stationHealthCts.Token);
        }

        private void StopStationHealthMonitor()
        {
            try
            {
                _stationHealthCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            _stationHealthCts?.Dispose();
            _stationHealthCts = null;
        }

        private void MarkAllStationsOffline(bool isOffline)
        {
            foreach (var station in Vm.EquipmentList.Stations)
            {
                if (string.Equals(station.Name, "All", StringComparison.OrdinalIgnoreCase))
                    continue;

                station.IsOffline = isOffline;
            }
        }

        private async Task RunStationHealthMonitorAsync(CancellationToken ct)
        {
            var periodSeconds = Math.Max(5, _configService.GetValue("StationHealth:PeriodSeconds", 15));
            var failThreshold = Math.Max(1, _configService.GetValue("StationHealth:FailCount", 3));

            var failCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodSeconds));

            try
            {
                await ProbeStationsOnceAsync(failCounts, failThreshold, ct);

                while (await timer.WaitForNextTickAsync(ct))
                {
                    await ProbeStationsOnceAsync(failCounts, failThreshold, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        private async Task ProbeStationsOnceAsync(Dictionary<string, int> failCounts, int failThreshold, CancellationToken ct)
        {
            // Snapshot коллекции берём через UI thread.
            var stations = await Dispatcher.InvokeAsync(() =>
                _equipmentListController.GetStationProbeItems());

            if (stations == null || stations.Count == 0)
                return;

            // Если глобально CtApi disconnected — сразу считаем станции offline.
            if (!_ctApiService.IsConnectionAvailable)
            {
                await Dispatcher.InvokeAsync(() => MarkAllStationsOffline(true));
                return;
            }

            foreach (var station in stations)
            {
                ct.ThrowIfCancellationRequested();

                var ok = await ProbeStationAsync(station);

                if (ok)
                {
                    failCounts[station.Name] = 0;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        station.IsOffline = false;
                    });

                    continue;
                }

                failCounts.TryGetValue(station.Name, out var fails);
                fails++;
                failCounts[station.Name] = fails;

                if (fails >= failThreshold)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        station.IsOffline = true;
                    });
                }
            }
        }

        private async Task<bool> ProbeStationAsync(StationStatusItem station)
        {
            try
            {
                var probeTag = (station.ProbeTagName ?? "").Trim();

                // Нет probe tag — не считаем станцию offline.
                if (string.IsNullOrWhiteSpace(probeTag))
                    return true;

                var result = await _ctApiService.TagReadAsync(probeTag);
                return result != "Unknown";
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Left pane init, Restore

        /// <summary>Начальное состояние: левая панель скрыта.</summary>
        private void InitLeftPaneState()
        {
            LeftPaneToggle.IsChecked = false;
            ApplyLeftPane(false);
        }

        /// <summary>
        /// После смены фильтров:
        /// - core фильтрации/выбора делает EquipmentListController,
        /// - orchestration-эффекты остаются в MainWindow.
        /// </summary>
        private void RestoreOrSelectEquipmentAfterFilterChanged(bool suppressAutoActivation = false)
        {
            var result = _equipmentListController.ApplyFiltersAndRestoreSelectionCore();

            if (!result.HasVisibleItems)
            {
                StopParamOverlayWait();

                if (!suppressAutoActivation && !_uiStateController.IsRestoringState && SelectedMainTab == MainTabKind.Info)
                    _ = _infoController.LoadCurrentAsync();

                return;
            }

            //if (!result.HasVisibleItems)
            //{
            //    StopParamOverlayWait();

            //    if (SelectedMainTab == MainTabKind.Param)
            //    {
            //        StopParamPolling();

            //        Vm.Param.CurrentParamModel = null;
            //        Vm.Param.ParamDiRows.Clear();
            //        Vm.Param.ParamDoRows.Clear();
            //        Vm.Param.ParamPlcRows.Clear();
            //        Vm.Param.DryRunModel = null;
            //        Vm.Param.LinkedAtvModel = null;
            //        Vm.Param.CurrentParamSettingsPage = ParamSettingsPage.None;
            //        Vm.Shell.ParamStatusText = "";
            //    }

            //    if (!suppressAutoActivation && !_uiStateController.IsRestoringState && SelectedMainTab == MainTabKind.Info)
            //        _ = _infoController.LoadCurrentAsync();

            //    return;
            //}

            if (_uiStateController.IsRestoringState || suppressAutoActivation)
                return;

            if (SelectedMainTab == MainTabKind.Param)
                StartParamPolling();
            else if (SelectedMainTab == MainTabKind.Info)
                _ = _infoController.LoadCurrentAsync();
        }

        #endregion

        #region Tab

        /// <summary>
        /// При активации вкладки делаем действие как по кнопке:
        /// SOE -> Load SOE,
        /// DB вкладки -> Search/Load DB.
        /// </summary>
        private async Task OnTabActivatedLikeSearchAsync(bool force)
        {
            // Уходим с Param:
            // останавливаем polling и сбрасываем ожидание overlay/progress.
            if ((MainTabKind)SelectedMainTabIndex != MainTabKind.Param)
            {
                StopParamPolling();
                StopParamOverlayWait();
            }

            switch ((MainTabKind)SelectedMainTabIndex)
            {
                case MainTabKind.Param:
                    StartParamPolling();
                    break;

                case MainTabKind.Info:
                    await _infoController.LoadCurrentAsync();
                    break;

                case MainTabKind.OperationActions:
                case MainTabKind.AlarmHistory:
                    await _dbController.LoadCurrentTabAsync(force);
                    break;

                case MainTabKind.SOE:
                    // Для group node SOE скрыт и не загружается
                    if (EquipVm.SelectedListBoxEquipment?.IsGroup == true)
                        return;

                    await LoadSoeFromUiAsync();
                    break;

                case MainTabKind.Message:
                    await _messageController.LoadAsync(silent: true);
                    break;

                default:
                    break;
            }
        }

        #endregion

        #region ListBox

        /// <summary>
        /// Клик по дереву оборудования.
        /// Child nodes работают как обычный equipment.
        /// Group nodes тоже теперь разрешены:
        /// - Param пока не загружаем (оставляем пустым),
        /// - Info/DB работают по Equipment группы,
        /// - SOE скрывается/не загружается.
        /// </summary>
        private void EquipmentsTree_SelectedItemChanged(object sender, SelectedItemChangedEventArgs e)
        {
            var selected = EquipVm.SelectedListBoxEquipment;

            if (_equipmentListController.SuppressEquipNameFromSelection)
                return;

            if (_suppressStartupTreeSelectionSideEffects)
                return;

            if (SearchTextEdit?.IsKeyboardFocusWithin == true)
                return;

            if (_uiStateController.IsRestoringState)
                return;

            var selectedEquipName = selected?.Equipment?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(selectedEquipName))
            {
                EquipVm.EquipName = selectedEquipName;
                _equipmentListController.RememberEquipmentForCurrentFilters(selectedEquipName);
            }

            // ===== GROUP NODE =====
            if (selected?.IsGroup == true)
            {
                StopParamPolling();
                StopParamOverlayWait();

                // Param пока оставляем пустым
                Vm.Param.CurrentParamModel = null;
                Vm.Param.ParamDiRows.Clear();
                Vm.Param.ParamDoRows.Clear();
                Vm.Param.ParamPlcRows.Clear();
                Vm.Param.DryRunModel = null;
                Vm.Param.LinkedAtvModel = null;
                Vm.Param.CurrentParamSettingsPage = ParamSettingsPage.None;
                Vm.Shell.ParamStatusText = "";

                // Если сейчас открыта Info — перегружаем Info для group equipment
                if (SelectedMainTab == MainTabKind.Info)
                {
                    _ = _infoController.LoadCurrentAsync();
                    return;
                }

                // Если сейчас открыты DB tabs — оставляем пользователя на них
                // и перегружаем по Equipment группы
                if (SelectedMainTab == MainTabKind.OperationActions ||
                    SelectedMainTab == MainTabKind.AlarmHistory)
                {
                    _ = _dbController.LoadCurrentTabAsync(force: true);
                    return;
                }

                // Если пользователь был на SOE — уводим с неё,
                // потому что для group node SOE сейчас недоступен
                if (SelectedMainTab == MainTabKind.SOE)
                {
                    SelectedMainTabIndex = (int)MainTabKind.Param;
                    return;
                }

                // Для остальных случаев старое поведение:
                // остаёмся/переходим на Param, но она будет пустой.
                if (SelectedMainTab != MainTabKind.Param)
                {
                    SelectedMainTabIndex = (int)MainTabKind.Param;
                    return;
                }

                return;
            }

            // ===== NORMAL EQUIPMENT / CHILD NODE =====

            if (!string.IsNullOrWhiteSpace(selectedEquipName))
            {
                if (SelectedMainTab == MainTabKind.Param)
                    BeginParamOverlayWait(selectedEquipName, Vm.Param.CurrentParamSettingsPage, needMainModel: true);
                else
                    StopParamOverlayWait();
            }
            else
            {
                StopParamOverlayWait();
            }

            if (SelectedMainTab == MainTabKind.Info)
            {
                _ = _infoController.LoadCurrentAsync();
                return;
            }

            if (SelectedMainTab == MainTabKind.OperationActions ||
                SelectedMainTab == MainTabKind.AlarmHistory)
            {
                _ = _dbController.LoadCurrentTabAsync(force: true);
                return;
            }

            if (SelectedMainTab == MainTabKind.SOE)
            {
                _ = LoadSoeFromUiAsync();
                return;
            }

            if (SelectedMainTab != MainTabKind.Param)
            {
                SelectedMainTabIndex = (int)MainTabKind.Param;
                return;
            }

            StartParamPolling();
        }

        #endregion

        #region Buttons

        /// <summary>
        /// Кнопка Load: загружает SOE по EquipName или выделенному элементу.
        /// </summary>
        private async void Load_Click(object sender, RoutedEventArgs e)
        {
            var text = (EquipVm.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var sel = EquipVm.SelectedListBoxEquipment?.Equipment;
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            await _soeController.LoadAndShowAsync(text);
        }

        /// <summary>
        /// Cancel: отменяет текущую загрузку SOE.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _soeController.Cancel();
        }

        /// <summary>
        /// Переключатель левой панели (показать/скрыть).
        /// </summary>
        private void LeftPaneToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_layoutReady) return;

            bool show = LeftPaneToggle.IsChecked == true;
            ApplyLeftPane(show);
        }

        /// <summary>
        /// Применяет ширину левой панели (скрывает/показывает).
        /// </summary>
        private void ApplyLeftPane(bool show)
        {
            if (LeftCol == null || SepCol == null) return;

            if (show)
            {
                if (_leftSavedWidth.Value <= 0)
                    _leftSavedWidth = new GridLength(350);

                LeftCol.Width = _leftSavedWidth;
                SepCol.Width = new GridLength(1);
            }
            else
            {
                if (LeftCol.Width.Value > 0)
                    _leftSavedWidth = LeftCol.Width;

                LeftCol.Width = new GridLength(0);
                SepCol.Width = new GridLength(0);
            }
        }

        /// <summary>
        /// Единая кнопка для всех вкладок:
        /// SOE -> грузим SOE по EquipName
        /// DB  -> грузим данные выбранной DB вкладки (с фильтром EquipName и датой DbDate)
        /// </summary>
        private async void MainAction_Click(object sender, RoutedEventArgs e)
        {
            switch (SelectedMainTab)
            {
                case MainTabKind.SOE:
                    await LoadSoeFromUiAsync();
                    break;

                case MainTabKind.Info:
                    await _infoController.LoadCurrentAsync();
                    break;

                case MainTabKind.OperationActions:
                case MainTabKind.AlarmHistory:
                    await _dbController.LoadCurrentTabAsync(force: true);
                    break;

                case MainTabKind.Message:
                    await _messageController.LoadAsync(silent: false);
                    break;

                default:
                    // на будущие вкладки
                    await _dbController.LoadCurrentTabAsync(force: true);
                    break;
            }
        }

        /// <summary>SOE: читаем имя из UI (выделение/поиск) и загружаем таблицу</summary>
        private async Task LoadSoeFromUiAsync()
        {
            if (EquipVm.SelectedListBoxEquipment?.IsGroup == true)
            {
                equipmentSOEDtos.Clear();
                StopParamOverlayWait();
                return;
            }

            if (Vm.Shell.IsLoading) return;

            var text = (EquipVm.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // если есть выделение — грузим его
            var sel = EquipVm.SelectedListBoxEquipment?.Equipment;
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            await _soeController.LoadAndShowAsync(text);
        }

        /// <summary>
        /// Верхняя toolbar-кнопка Scan QR:
        /// сканирует QR -> пишет во ExternalTag -> выполняет поиск и открывает Param.
        /// </summary>
        private async void ToolbarScanQr_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Param_ScanQrToExternalTagAndSearchAsync();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, ex.Message, "Scan QR", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Logo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isLogoLoginToggleInProgress)
                return;

            _isLogoLoginToggleInProgress = true;

            try
            {
                var toggleUser = (_configService["CtApiSecurity:ToggleLoginUser"] ?? "").Trim();
                var togglePassword = (_configService["CtApiSecurity:ToggleLoginPassword"] ?? "").Trim();

                if (string.IsNullOrWhiteSpace(toggleUser))
                {
                    DXMessageBox.Show(this, "Toggle login user is not configured.", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Current SCADA login name
                var currentUserName = (await _ctApiService.UserInfoAsync(1)).Trim();
                var currentFullName = (await _ctApiService.UserInfoAsync(2)).Trim();

                var isTabLoggedIn = currentUserName.Equals(toggleUser, StringComparison.OrdinalIgnoreCase) || currentFullName.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isTabLoggedIn)
                {
                    await _ctApiService.LogoutAsync();

                    DXMessageBox.Show(this, "Tab user has been logged out.", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var loginResult = await _ctApiService.LoginAsync(toggleUser, togglePassword);

                // Plant SCADA Login returns 0 on success
                if (string.Equals(loginResult?.Trim(), "0", StringComparison.OrdinalIgnoreCase))
                    DXMessageBox.Show(this, "Tab user has been logged in.", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    DXMessageBox.Show(this, $"Login failed. Result: {loginResult}", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, $"Unable to toggle login state.\n\n{ex.Message}", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLogoLoginToggleInProgress = false;
            }
        }

        #endregion

        #region Param

        /// <summary>
        /// Определяет, поддерживает ли текущая модель конкретную страницу Param.
        /// Опираемся только на новую архитектуру IParamModel / SupportedPages.
        /// </summary>
        private bool CurrentParamSupportsPage(ParamSettingsPage page)
        {
            if (Vm.Param.CurrentParamModel is not IParamModel paramModel)
                return false;

            return paramModel.SupportedPages.Contains(page);
        }

        /// <summary>
        /// Единая точка переключения Chart / Settings для всех ParamView.
        /// Вся навигация между страницами Param должна идти только через этот метод.
        /// </summary>
        public void ShowParamPage(ParamSettingsPage page)
        {
            if (page != ParamSettingsPage.None && !CurrentParamSupportsPage(page))
                return;

            var (equipName, _, _) = ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();

            SetParamSettingsPage(page);

            if (page == ParamSettingsPage.None)
            {
                StopParamOverlayWait();
                ShowParamChart(reset: true);
                return;
            }

            ShowParamSettings();

            // При клике по кнопкам страниц equipment не меняется,
            // поэтому main model уже есть и ждём только саму секцию.
            if (page == ParamSettingsPage.DiDo &&
                string.Equals(_lastSectionLoadedEquipName, equipName, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == ParamSettingsPage.DiDo &&
                IsFinalParamLoadState(_lastSectionLoadedState))
            {
                StopParamOverlayWait();
            }
            else
            {
                BeginParamOverlayWait(equipName, page, needMainModel: false);
            }

            _ = RefreshActiveParamSectionSafeAsync();
        }

        /// <summary>
        /// Безопасное обновление активной Param-секции после клика по кнопке.
        /// </summary>
        private async Task RefreshActiveParamSectionSafeAsync()
        {
            try
            {
                if (!_ctApiService.IsConnectionAvailable)
                    return;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _paramRefsController.RefreshActiveParamSectionAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                StopParamOverlayWait();
            }
            catch (Exception ex)
            {
                Vm.Shell.ParamStatusText = $"Param settings refresh error: {ex.Message}";
                StopParamOverlayWait();
            }
        }

        /// <summary>
        /// Начинаем ожидание обновления центральной области Param.
        /// 
        /// needMainModel = true  -> ждём и основную модель, и секцию
        /// needMainModel = false -> ждём только секцию
        /// </summary>
        private void BeginParamOverlayWait(string? equipName, ParamSettingsPage page, bool needMainModel)
        {
            var key = (equipName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                StopParamOverlayWait();
                return;
            }

            _pendingParamOverlayEquipName = key;
            _pendingParamOverlayPage = page;
            _pendingParamOverlayNeedsMainModel = needMainModel;

            var mainDone =
                string.Equals(_lastMainLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                IsFinalParamLoadState(_lastMainLoadedState);

            // Chart: ждём только main model
            if (page == ParamSettingsPage.None)
            {
                Vm.Shell.IsParamCenterLoading = !mainDone;
                return;
            }

            var sectionDone =
                string.Equals(_lastSectionLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == page &&
                IsFinalParamLoadState(_lastSectionLoadedState);

            // При простом переключении страницы ждём только секцию
            if (!needMainModel)
            {
                Vm.Shell.IsParamCenterLoading = !sectionDone;
                return;
            }

            // При смене equipment ждём и модель, и секцию
            Vm.Shell.IsParamCenterLoading = !(mainDone && sectionDone);
        }

        /// <summary>
        /// Полностью останавливаем ожидание overlay.
        /// </summary>
        private void StopParamOverlayWait()
        {
            _pendingParamOverlayEquipName = null;
            _pendingParamOverlayPage = ParamSettingsPage.None;
            _pendingParamOverlayNeedsMainModel = false;
            Vm.Shell.IsParamCenterLoading = false;
        }

        /// <summary>
        /// Проверяем, можно ли уже скрывать overlay.
        /// </summary>
        private void TryFinishParamOverlayWait()
        {
            if (!Vm.Shell.IsParamCenterLoading)
                return;

            var key = (_pendingParamOverlayEquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                StopParamOverlayWait();
                return;
            }

            var mainDone =
                string.Equals(_lastMainLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                IsFinalParamLoadState(_lastMainLoadedState);

            // Chart: ждём только main
            if (_pendingParamOverlayPage == ParamSettingsPage.None)
            {
                if (mainDone)
                    StopParamOverlayWait();

                return;
            }

            var sectionDone =
                string.Equals(_lastSectionLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == _pendingParamOverlayPage &&
                IsFinalParamLoadState(_lastSectionLoadedState);

            // Переключение страницы без смены equipment:
            // ждём только секцию
            if (!_pendingParamOverlayNeedsMainModel)
            {
                if (sectionDone)
                    StopParamOverlayWait();

                return;
            }

            // Полная смена equipment:
            // ждём и main, и section
            if (mainDone && sectionDone)
                StopParamOverlayWait();
        }

        /// <summary>
        /// Уведомление: основная Param-модель по equipment завершила загрузку.
        /// </summary>
        private void NotifyMainParamLoadedCore(string? equipName, ParamLoadState state)
        {
            void Apply()
            {
                _lastMainLoadedEquipName = (equipName ?? "").Trim();
                _lastMainLoadedState = state;
                TryFinishParamOverlayWait();
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)Apply);
                return;
            }

            Apply();
        }

        /// <summary>
        /// Уведомление: конкретная Param settings-секция завершила загрузку.
        /// </summary>
        private void NotifySectionLoadedCore(string? equipName, ParamSettingsPage page, ParamLoadState state)
        {
            void Apply()
            {
                _lastSectionLoadedEquipName = (equipName ?? "").Trim();
                _lastSectionLoadedPage = page;
                _lastSectionLoadedState = state;
                TryFinishParamOverlayWait();
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)Apply);
                return;
            }

            Apply();
        }

        private (string equipName, string equipType, string equipDescription) ResolveSelectedEquipForParam()
        {
            var selected = EquipVm.SelectedListBoxEquipment;
            if (selected == null)
                return ("", "", "");

            return (selected.Equipment ?? "", selected.Type ?? "", selected.Description ?? "");
        }

        public async Task Param_SetFavoriteAsync(bool isFavorite)
        {
            var selected = EquipVm.SelectedListBoxEquipment;
            if (selected == null || selected.IsGroup)
                return;

            var equipName = (selected.Equipment ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            var oldValue = !isFavorite;

            try
            {
                if (!Vm.Info.IsInfoDbConnected)
                    throw new InvalidOperationException("Info DB is disconnected.");

                await _equipInfoService.SetFavoriteAsync(equipName, isFavorite);

                SetFavoriteFlagInLoadedEquipments(equipName, isFavorite);

                if (Vm.Info.CurrentEquipInfo != null &&
                    string.Equals((Vm.Info.CurrentEquipInfo.EquipName ?? "").Trim(), equipName, StringComparison.OrdinalIgnoreCase))
                {
                    Vm.Info.CurrentEquipInfo.IsFavorite = isFavorite;
                }

                if (EquipVm.SelectedTypeFilter == EquipTypeGroup.Favorites)
                    RestoreOrSelectEquipmentAfterFilterChanged();

                Vm.Shell.ParamStatusText = isFavorite
                    ? $"Added to favorites: {equipName}"
                    : $"Removed from favorites: {equipName}";
            }
            catch (Exception ex)
            {
                SetFavoriteFlagInLoadedEquipments(equipName, oldValue);

                if (Vm.Info.CurrentEquipInfo != null &&
                    string.Equals((Vm.Info.CurrentEquipInfo.EquipName ?? "").Trim(), equipName, StringComparison.OrdinalIgnoreCase))
                {
                    Vm.Info.CurrentEquipInfo.IsFavorite = oldValue;
                }

                if (EquipVm.SelectedTypeFilter == EquipTypeGroup.Favorites)
                    _equipmentListController.ApplyFilters();

                Vm.Shell.ParamStatusText = $"Favorite save error: {ex.Message}";
                DXMessageBox.Show(this, ex.Message, "Favorite", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Param polling

        private void StartParamPolling()
        {
            _paramController?.Start();
        }

        private void StopParamPolling()
        {
            _paramController?.Stop();
        }

        #endregion

        #region Param Write
        /// <summary>
        /// PLC: запись значения из UI (SimpleButton и т.п.).
        /// Теперь вся логика в ParamWriteController.
        /// </summary>
        public async void ParamPlc_WriteFromUi(PlcRefRow row, object? newValue)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.WritePlcFromUiAsync(row, newValue);
        }

        /// <summary>
        /// DevExpress EditValueChanged (CheckEdit и др.) -> запись параметров.
        /// </summary>
        public async void ParamEditable_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.OnEditValueChangedAsync(sender, e);
        }

        /// <summary>
        /// KeyDown/PreviewKeyDown: запись по Enter.
        /// </summary>
        public async void ParamEditable_EditValueChanged(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.OnPreviewKeyDownAsync(sender, e);
        }

        /// <summary>
        /// Универсальная запись параметра (если не DevExpress событие).
        /// </summary>
        public async void ParamEditable_WriteFromUi(string? equipItem, object? newValue, object? oldValue)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.WriteFromUiAsync(equipItem, newValue, oldValue);
        }

        public void BeginParamFieldEdit()
        {
            Interlocked.Exchange(ref _isEditingField, 1);
        }

        // Сбрасываем, когда пользователь закончил редактирование (lost focus или Enter)
        public void EndParamFieldEdit()
        {
            Interlocked.Exchange(ref _isEditingField, 0);
        }

        #endregion

        #region Param Refs

        /// <summary>
        /// Устанавливает активную страницу Param settings.
        /// Вынесено в ParamRefsController.
        /// </summary>
        public void SetParamSettingsPage(ParamSettingsPage page)
            => _paramRefsController.SetParamSettingsPage(page);

        /// <summary>
        /// Переход по клику из DI/DO списка к связанному оборудованию.
        /// Вынесено в ParamRefsController.
        /// </summary>
        public void Param_NavigateToLinkedEquip(DiDoRefRow? row)
            => _paramRefsController.NavigateToLinkedEquip(row);

        /// <summary>
        /// Переход к связанному оборудованию по имени.
        /// Вынесено в ParamRefsController.
        /// </summary>
        public void Param_NavigateToLinkedEquip(string? equipName)
            => _paramRefsController.NavigateToLinkedEquip(equipName);

        #endregion

        #region Trend

        // прокси для View
        public void OnParamChartUserRangeChanged(DateTime minLocal, DateTime maxLocal)
            => _trendController.OnUserRangeChanged(minLocal, maxLocal);

        public void SetParamChartLiveMode(bool resetPoints = false)
            => _trendController.SetLiveMode(resetPoints);

        public void ShowParamChart(bool reset = false)
            => _trendController.ShowChart(reset);

        public void ShowParamSettings()
            => _trendController.ShowSettings();

        /// <summary>
        /// Called from AIParamView.ParamChart_BoundDataChanged.
        /// Re-applies [TrendSeriesStyle] attributes to recreated DevExpress series.
        /// </summary>
        public void ApplyTrendSeriesStyles(ChartControl chart)
            => TrendSeriesStyler.Apply(chart, Vm.Param.CurrentParamModel);
        #endregion

        #region QR-Code       

        /// <summary>
        /// Param tab: генерирует QR по текущему тексту поиска (EquipName) или выбранному оборудованию.
        /// Автосохраняет в .\QRCodes\Station\Type\*.png и показывает DevExpress окно об успехе.
        /// </summary>
        public Task Param_GenerateQrAsync() => _qrController.GenerateQrAsync();

        /// <summary>
        /// Param tab: сканирует QR с камеры.
        /// Затем:
        /// 1) выставляет Station/Type фильтры по найденному оборудованию,
        /// 2) пишет в ExternalTag (best-effort),
        /// 3) подставляет в поиск,
        /// 4) выделяет оборудование,
        /// 5) переключает на Param и запускает polling.
        /// </summary>
        public Task Param_ScanQrToExternalTagAndSearchAsync() => _qrController.ScanQrToExternalTagAndSearchAsync();

        /// <summary>
        /// Проверяет, существует ли уже QR PNG файл для текущего текста (с учётом Station\TypeGroup папки).
        /// Это используется для скрытия кнопки Generate QR.
        /// </summary>
        public bool Param_IsQrAlreadyGenerated() => _qrController.IsQrAlreadyGenerated();

        /// <summary>
        /// True => показываем кнопку Generate QR.
        /// False => прячем (нет текста для QR или файл уже существует).
        /// Используется в XAML через BoolToVis.
        /// </summary>
        public bool Param_ShowGenerateQrButton => _qrController.ShowGenerateQrButton;

        /// <summary>
        /// Уведомляет UI, что нужно пересчитать Visibility кнопки Generate QR.
        /// </summary>
        private void NotifyParamQrUiChanged()
        {
            OnPropertyChanged(nameof(Param_ShowGenerateQrButton));
        }

        /// <summary>
        /// Массовая генерация QR-кодов по всему оборудованию.
        /// Используется из Application settings.
        /// </summary>
        public Task<BulkQrGenerateResult> Settings_GenerateAllQrAsync()
            => _qrController.GenerateAllQrAsync();

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion

        #region Helpers

        /// <summary>
        /// Возвращает имя оборудования для записи.
        ///
        /// По умолчанию запись идёт в текущее выбранное оборудование (EquipName).
        /// Но:
        /// - если отправитель находится в секции DryRun (DataContext = DryRunMotor),
        ///   то писать нужно в DryRunEquipName;
        /// - если отправитель находится в секции linked ATV (DataContext = AtvParam)
        ///   и сейчас открыт Motor -> ATV page,
        ///   то писать нужно в LinkedAtvEquipName.
        /// </summary>
        private string ResolveEquipNameForWrite(object sender)
        {
            // Обычная запись — в текущее выбранное оборудование
            var currentEquip = (EquipVm.EquipName ?? "").Trim();

            if (sender is FrameworkElement fe)
            {
                // DryRun секция работает с другим target-equipment
                if (fe.DataContext is DryRunMotor)
                {
                    var dryRunEquip = (Vm.Param.DryRunEquipName ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(dryRunEquip))
                        return dryRunEquip;

                    return currentEquip;
                }

                // ATV секция внутри Motor работает с linked ATV equipment
                if (fe.DataContext is AtvParam)
                {
                    var (_, equipType, _) = ResolveSelectedEquipForParam();
                    var currentGroup = EquipTypeRegistry.GetGroup(equipType ?? "");

                    if (currentGroup == EquipTypeGroup.Motor &&
                        Vm.Param.CurrentParamSettingsPage == ParamSettingsPage.Atv)
                    {
                        var linkedAtvEquip = (Vm.Param.LinkedAtvEquipName ?? "").Trim();

                        if (!string.IsNullOrWhiteSpace(linkedAtvEquip))
                            return linkedAtvEquip;
                    }
                }
            }

            return currentEquip;
        }

        private void SubscribeEquipmentListBridge()
        {
            EquipVm.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(EquipmentListViewModel.EquipName):
                        if (_uiStateController.IsRestoringState)
                        {
                            NotifyParamQrUiChanged();
                            break;
                        }

                        _equipmentListController.ScheduleSearch(EquipVm.EquipName);
                        _uiStateController.ScheduleSave();
                        NotifyParamQrUiChanged();
                        break;

                    case nameof(EquipmentListViewModel.SelectedListBoxEquipment):
                        NotifyParamQrUiChanged();
                        break;

                    case nameof(EquipmentListViewModel.SelectedTypeFilter):
                    case nameof(EquipmentListViewModel.SelectedStation):
                        if (_uiStateController.IsRestoringState || _suppressStartupTreeSelectionSideEffects)
                        {
                            NotifyParamQrUiChanged();
                            break;
                        }

                        RestoreOrSelectEquipmentAfterFilterChanged();
                        _uiStateController.ScheduleSave();
                        NotifyParamQrUiChanged();
                        break;
                }
            };
        }

        private void StopBackgroundWorkForShutdown()
        {
            try
            {
                StopStationHealthMonitor();
            }
            catch { }

            try
            {
                StopParamPolling();
            }
            catch { }

            try
            {
                _soeController?.Cancel();
            }
            catch { }

            try
            {
                _dbController?.CancelCurrentLoad();
            }
            catch { }

            try
            {
                _equipListCts?.Cancel();
            }
            catch { }
        }

        private async Task ApplyFavoriteFlagsAsync(IReadOnlyCollection<EquipListBoxItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0)
                return;

            IReadOnlyCollection<string> favoriteNames = Array.Empty<string>();

            if (Vm.Info.IsInfoDbConnected)
            {
                try
                {
                    favoriteNames = await _equipInfoService.GetFavoriteEquipNamesAsync(ct);
                }
                catch
                {
                    favoriteNames = Array.Empty<string>();
                }
            }

            var favoriteSet = new HashSet<string>(favoriteNames, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                var equipName = (item.Equipment ?? "").Trim();

                item.IsFavorite =
                    !item.IsGroup &&
                    !string.IsNullOrWhiteSpace(equipName) &&
                    favoriteSet.Contains(equipName);
            }
        }

        private void SetFavoriteFlagInLoadedEquipments(string equipName, bool isFavorite)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            foreach (var item in EquipVm.Equipments)
            {
                if (item.IsGroup)
                    continue;

                if (string.Equals((item.Equipment ?? "").Trim(), equipName, StringComparison.OrdinalIgnoreCase))
                    item.IsFavorite = isFavorite;
            }
        }

        private async Task ApplyInfoIndicatorFlagsAsync(IReadOnlyCollection<EquipListBoxItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0)
                return;

            IReadOnlyCollection<string> photoNames = Array.Empty<string>();
            IReadOnlyCollection<string> instructionNames = Array.Empty<string>();
            IReadOnlyCollection<string> schemeNames = Array.Empty<string>();
            IReadOnlyCollection<string> notesNames = Array.Empty<string>();

            if (Vm.Info.IsInfoDbConnected)
            {
                try
                {
                    photoNames = await _equipInfoService.GetEquipNamesWithLinkedPhotosAsync(ct);
                    instructionNames = await _equipInfoService.GetEquipNamesWithLinkedInstructionsAsync(ct);
                    schemeNames = await _equipInfoService.GetEquipNamesWithLinkedSchemesAsync(ct);
                    notesNames = await _equipInfoService.GetEquipNamesWithNotesAsync(ct);
                }
                catch
                {
                    photoNames = Array.Empty<string>();
                    instructionNames = Array.Empty<string>();
                    schemeNames = Array.Empty<string>();
                    notesNames = Array.Empty<string>();
                }
            }

            var photoSet = new HashSet<string>(photoNames, StringComparer.OrdinalIgnoreCase);
            var instructionSet = new HashSet<string>(instructionNames, StringComparer.OrdinalIgnoreCase);
            var schemeSet = new HashSet<string>(schemeNames, StringComparer.OrdinalIgnoreCase);
            var notesSet = new HashSet<string>(notesNames, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                var equipName = (item.Equipment ?? "").Trim();

                if (string.IsNullOrWhiteSpace(equipName))
                {
                    item.HasLinkedImage = false;
                    item.HasLinkedInstruction = false;
                    item.HasLinkedScheme = false;
                    item.HasNotes = false;
                    continue;
                }

                item.HasLinkedImage = photoSet.Contains(equipName);
                item.HasLinkedInstruction = instructionSet.Contains(equipName);
                item.HasLinkedScheme = schemeSet.Contains(equipName);
                item.HasNotes = notesSet.Contains(equipName);
            }
        }

        private void SetLinkedImageFlagInLoadedEquipments(string equipName, bool hasLinkedImage)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            foreach (var item in EquipVm.Equipments)
            {
                if (string.Equals((item.Equipment ?? "").Trim(), equipName, StringComparison.OrdinalIgnoreCase))
                    item.HasLinkedImage = hasLinkedImage;
            }
        }

        private void SetLinkedImageFlagsInLoadedEquipments(IEnumerable<string> equipNames, bool hasLinkedImage)
        {
            if (equipNames == null)
                return;

            var set = new HashSet<string>(
                equipNames
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (set.Count == 0)
                return;

            foreach (var item in EquipVm.Equipments)
            {
                var equipName = (item.Equipment ?? "").Trim();

                if (set.Contains(equipName))
                    item.HasLinkedImage = hasLinkedImage;
            }
        }

        private void SetInfoIndicatorFlagsInLoadedEquipments(string equipName, bool? hasLinkedImage = null, bool? hasLinkedInstruction = null, bool? hasLinkedScheme = null, bool? hasNotes = null)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            foreach (var item in EquipVm.Equipments)
            {
                if (!string.Equals((item.Equipment ?? "").Trim(), equipName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (hasLinkedImage.HasValue)
                    item.HasLinkedImage = hasLinkedImage.Value;

                if (hasLinkedInstruction.HasValue)
                    item.HasLinkedInstruction = hasLinkedInstruction.Value;

                if (hasLinkedScheme.HasValue)
                    item.HasLinkedScheme = hasLinkedScheme.Value;

                if (hasNotes.HasValue)
                    item.HasNotes = hasNotes.Value;
            }
        }

        #endregion

        #region Settings

        /// <summary>
        /// Глобальная горячая клавиша окна:
        /// F10 -> открыть модальное окно редактирования appsettings.json.
        /// </summary>
        private void ThemedWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.SystemKey != Key.F10)
                return;

            e.Handled = true;
            ShowAppSettingsWindow();
        }

        /// <summary>
        /// Открывает модальное окно настроек.
        /// Редактируется тот же appsettings.json, который читает Host:
        /// AppContext.BaseDirectory\appsettings.json
        /// </summary>
        private void ShowAppSettingsWindow()
        {
            try
            {
                var settingsPath = GetRuntimeAppSettingsPath();

                var wnd = new AppSettingsWindow(
                    settingsPath,
                    generateAllQrAsync: Settings_GenerateAllQrAsync)
                {
                    Owner = this
                };

                wnd.ShowDialog();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show($"Failed to open settings window.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Возвращает путь к runtime appsettings.json.
        /// Это важно:
        /// приложение читает именно файл рядом с exe, а не исходник из корня проекта.
        /// </summary>
        private static string GetRuntimeAppSettingsPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }

        #endregion

        #region Info

        /// <summary>
        /// Тонкий прокси для InfoTabHost.xaml.cs
        /// </summary>
        public Task LoadInfoForCurrentEquipmentAsync()
            => _infoController.LoadCurrentAsync();

        /// <summary>
        /// Тонкий прокси для InfoTabHost.xaml.cs
        /// </summary>
        public async Task Info_BeginEditAsync() => await _infoController.BeginEditAsync();

        /// <summary>
        /// Тонкий прокси для InfoTabHost.xaml.cs
        /// </summary>
        public async Task Info_SaveAsync()
        {
            await _infoController.SaveAsync(Vm.Shell.CurrentCtUserName);

            var equipName = (Vm.Info.CurrentEquipInfo?.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            var info = Vm.Info.CurrentEquipInfo;

            SetInfoIndicatorFlagsInLoadedEquipments(
                equipName,
                hasLinkedImage: info?.Photos?.Count > 0,
                hasLinkedInstruction: info?.Instructions?.Count > 0,
                hasLinkedScheme: info?.Schemes?.Count > 0,
                hasNotes: info?.Notes?.Any(x => !string.IsNullOrWhiteSpace(x.NoteText)) == true);
        }

        /// <summary>
        /// Добавить фото с диска.
        /// </summary>
        public Task Info_LoadPhotoFilesAsync()
            => _infoController.LoadPhotoFilesAsync();

        /// <summary>
        /// Удалить выбранное фото из карточки.
        /// </summary>
        public void Info_RemoveSelectedPhoto()
            => _infoController.RemoveSelectedPhoto();

        /// <summary>
        /// Добавить PDF-файлы для текущей документной страницы.
        /// Также здесь обрабатывается Excel import для Scheme/Instruction.
        /// </summary>
        public async Task Info_LoadCurrentDocumentFilesAsync()
        {
            InfoDocumentImportResult? importResult = null;
            Exception? importError = null;
            var progressWasStarted = false;

            try
            {
                void UpdateProgress(int done, int total, string text)
                {
                    progressWasStarted = true;

                    Dispatcher.Invoke(() =>
                    {
                        Mouse.OverrideCursor = Cursors.Wait;

                        Vm.Shell.IsGlobalProgressActive = true;
                        Vm.Shell.GlobalProgressDone = done;
                        Vm.Shell.GlobalProgressTotal = Math.Max(1, total);
                        Vm.Shell.GlobalProgressText = text;
                    });
                }

                importResult = await _infoController.LoadCurrentDocumentFilesAsync(UpdateProgress);

                if (importResult != null)
                {
                    if (importResult.Kind == InfoFileKind.Scheme)
                    {
                        foreach (var equipName in importResult.AffectedEquipNames)
                        {
                            SetInfoIndicatorFlagsInLoadedEquipments(
                                equipName,
                                hasLinkedScheme: true);
                        }

                        Vm.Shell.BottomText =
                            $"Scheme import finished. Jobs: {importResult.ImportJobs}, added: {importResult.AddedToDb}, updated: {importResult.UpdatedInDb}, links: {importResult.LinkedExisting}, errors: {importResult.Errors}.";
                    }
                    else if (importResult.Kind == InfoFileKind.Instruction)
                    {
                        foreach (var equipName in importResult.PdfAffectedEquipNames)
                        {
                            SetInfoIndicatorFlagsInLoadedEquipments(
                                equipName,
                                hasLinkedInstruction: true);
                        }

                        foreach (var equipName in importResult.ImageAffectedEquipNames)
                        {
                            SetInfoIndicatorFlagsInLoadedEquipments(
                                equipName,
                                hasLinkedImage: true);
                        }

                        Vm.Shell.BottomText =
                            $"Instruction import finished. Info updated: {importResult.EquipmentInfoUpdated}, PDF links: {importResult.PdfLinksCreated}, image links: {importResult.ImageLinksCreated}, errors: {importResult.Errors}.";
                    }

                    Vm.Info.IsInfoEditMode = false;
                }
            }
            catch (Exception ex)
            {
                importError = ex;
            }
            finally
            {
                if (progressWasStarted)
                {
                    Vm.Shell.IsGlobalProgressActive = false;
                    Vm.Shell.GlobalProgressText = "";
                    Vm.Shell.GlobalProgressDone = 0;
                    Vm.Shell.GlobalProgressTotal = 0;

                    Mouse.OverrideCursor = null;
                }
            }

            if (progressWasStarted)
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var messageTitle = importResult?.Kind == InfoFileKind.Instruction
                ? "Instruction import"
                : "Scheme import";

            if (importError != null)
            {
                DXMessageBox.Show(
                    this,
                    $"{messageTitle} failed.\n\n{importError.Message}",
                    messageTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            if (importResult != null)
            {
                DXMessageBox.Show(
                    this,
                    importResult.ToMessage(messageTitle),
                    messageTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Удалить выбранный PDF с текущей документной страницы.
        /// </summary>
        public Task Info_RemoveCurrentDocumentAsync()
            => _infoController.RemoveCurrentDocumentAsync();

        /// <summary>
        /// Переключение страниц Info.
        /// </summary>
        public Task ShowInfoPageAsync(InfoPageKind page)
            => _infoController.ShowPageAsync(page);

        /// <summary>
        /// Подготовка preview после смены выбранного документа.
        /// </summary>
        public Task Info_OnCurrentDocumentSelectionChangedAsync()
            => _infoController.PrepareCurrentDocumentAsync();

        /// <summary>
        /// Выгрузить выбранный документ из БД/модели в локальный cache.
        /// </summary>
        public Task Info_ExportCurrentDocumentAsync()
            => _infoController.ExportCurrentDocumentAsync();

        /// <summary>
        /// Синхронизировать linked document files из checked-combo.
        /// </summary>
        public Task Info_OnDocumentLibraryEditValueChangedAsync()
            => _infoController.SyncCurrentDocumentSelectionFromLibraryAsync();

        public Task Info_CapturePhotoFromCameraAsync()
            => _infoController.CapturePhotoFromCameraAsync();

        public Task Info_OnSelectedPhotoChangedAsync()
        {
            return _infoController.EnsureSelectedPhotoLoadedAsync();
        }
        public Task Info_OnPhotoLibraryCheckChangedAsync(EquipmentInfoFileDto file)
        {
            return _infoController.OnPhotoLibraryCheckChangedAsync(file);
        }

        /// <summary>
        /// Запомнить текущую позицию просмотра PDF.
        /// </summary>
        public Task Info_RememberCurrentDocumentPositionAsync(PdfViewerControl viewer)
            => _infoController.RememberCurrentDocumentPositionAsync(viewer);

        /// <summary>
        /// Восстановить сохранённую позицию просмотра PDF после загрузки документа.
        /// </summary>
        public Task Info_RestoreCurrentDocumentPositionAsync(PdfViewerControl viewer)
            => _infoController.RestoreCurrentDocumentPositionAsync(viewer);

        /// <summary>
        /// Полностью удалить выбранное фото из shared DB library.
        /// </summary>
        public Task Info_DeleteSelectedPhotoFromDbAsync()
            => _infoController.DeleteSelectedPhotoFromDbAsync();

        /// <summary>
        /// Полностью удалить выбранный PDF из shared DB library.
        /// </summary>
        public Task Info_DeleteCurrentDocumentFromDbAsync()
            => _infoController.DeleteCurrentDocumentFromDbAsync();

        /// <summary>
        /// Умный импорт изображений из папки.
        /// Папка может быть:
        /// - сразу папкой типа: Motor\*.jpg
        /// - общей папкой с подпапками типов: Images\Motor\*.jpg
        /// </summary>
        public async Task Info_ImportImagesFromFolderAsync()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select image import folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            var dialogResult = dialog.ShowDialog();

            if (dialogResult != System.Windows.Forms.DialogResult.OK)
                return;

            var folderPath = dialog.SelectedPath;

            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            InfoImageImportResult? importResult = null;
            Exception? importError = null;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var equipments = Vm.EquipmentList.Equipments
                    .Where(x => x != null)
                    .Where(x => !x.IsGroup)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                    .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                void UpdateProgress(int done, int total, string text)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Vm.Shell.IsGlobalProgressActive = true;
                        Vm.Shell.GlobalProgressDone = done;
                        Vm.Shell.GlobalProgressTotal = Math.Max(1, total);
                        Vm.Shell.GlobalProgressText = text;
                    });
                }

                UpdateProgress(0, 1, "Preparing image import...");

                importResult = await _infoController.ImportImagesFromFolderAsync(folderPath, equipments, UpdateProgress);
                SetLinkedImageFlagsInLoadedEquipments(importResult.AffectedEquipNames, true);

                Vm.Shell.BottomText = $"Image import finished. Added: {importResult.AddedToDb}, linked existing: {importResult.LinkedExisting}, errors: {importResult.Errors}.";

                // Важно:
                // импорт уже напрямую записал фото/link в БД,
                // поэтому после завершения выходим из edit mode до показа окна результата.
                Vm.Info.IsInfoEditMode = false;
            }
            catch (Exception ex)
            {
                importError = ex;
            }
            finally
            {
                // Важно:
                // сбрасываем progress/cursor ДО модального DXMessageBox,
                // иначе busy cursor останется поверх окна результата.
                Vm.Shell.IsGlobalProgressActive = false;
                Vm.Shell.GlobalProgressText = "";
                Vm.Shell.GlobalProgressDone = 0;
                Vm.Shell.GlobalProgressTotal = 0;

                Mouse.OverrideCursor = null;
            }

            // Даём WPF один проход на перерисовку:
            // кнопки edit-mode спрячутся, верхние кнопки вернутся,
            // bottom progress исчезнет, cursor станет обычным.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            if (importError != null)
            {
                DXMessageBox.Show(
                    this,
                    $"Image import failed.\n\n{importError.Message}",
                    "Image import",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            if (importResult != null)
            {
                DXMessageBox.Show(
                    this,
                    importResult.ToMessage(),
                    "Image import",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        public async Task Info_ApplyProductCodeFromUiAsync(string? productCode) => await _infoController.ApplyProductCodeFromUiAsync(productCode);

        public void Info_AddNewNote() => _infoController.AddNewNote(Vm.Shell.CurrentCtUserName);

        public async Task Info_DeleteSelectedNoteAsync() => await _infoController.DeleteSelectedNoteAsync();

        #endregion

        #region Message

        public Task Message_LoadAsync()
            => _messageController.LoadAsync(silent: false);

        public void Message_AddNew()
            => _messageController.AddNew();

        public void Message_BeginEditSelected()
            => _messageController.BeginEditSelected();

        public Task Message_SaveSelectedAsync()
            => _messageController.SaveSelectedAsync();

        public Task Message_DeleteSelectedAsync()
            => _messageController.DeleteSelectedAsync();

        public Task Message_ToggleActivitySelectedAsync()
            => _messageController.ToggleActivitySelectedAsync();

        public Task Message_ToggleShowAllAsync()
            => _messageController.ToggleShowAllAsync();

        public void Message_SelectedMessageChanged()
            => _messageController.OnSelectedMessageChanged();

        #endregion
    }
}