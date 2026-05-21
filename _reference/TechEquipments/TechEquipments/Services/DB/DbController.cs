using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TechEquipments.ViewModels;

namespace TechEquipments
{
    /// <summary>
    /// Контролер DB-вкладок (Operation actions / Alarm history):
    /// - перевірка коннекту
    /// - debounce автоперезавантаження при зміні дати
    /// - cancel попереднього запиту при новому
    /// - gate (щоб не було паралельних DB-запитів)
    ///
    /// Працює напряму з MainViewModel, без IDbHost.
    /// </summary>
    public sealed class DbController
    {
        private readonly IDbService _dbService;
        private readonly MainViewModel _vm;
        private readonly Dispatcher _dispatcher;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private CancellationTokenSource? _cts;

        private readonly DispatcherTimer _reloadTimer;

        private readonly record struct DbQueryKey(DateTime Date, string Filter);

        private DbQueryKey? _lastOpActsQuery;
        private DbQueryKey? _lastAlarmQuery;

        public DbController(IDbService dbService, MainViewModel vm, Dispatcher dispatcher)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            // debounce-таймер на UI Dispatcher
            _reloadTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            _reloadTimer.Tick += async (_, __) =>
            {
                _reloadTimer.Stop();

                // тільки якщо DB вкладки і є коннект
                if (!IsDbTabSelected || !_vm.Database.IsDbConnected)
                    return;

                try
                {
                    await LoadCurrentTabAsync(force: true);
                }
                catch (Exception ex)
                {
                    _vm.Shell.BottomText = $"DB reload error: {ex.Message}";
                }
            };
        }

        private MainTabKind SelectedMainTab => _vm.SelectedMainTab;

        private bool IsDbTabSelected => SelectedMainTab is MainTabKind.OperationActions or MainTabKind.AlarmHistory;

        private DateTime DbDate => _vm.Database.DbDate.Date;

        private string DbFilter => (_vm.EquipmentList.EquipName ?? "").Trim();

        /// <summary>
        /// Перевірка підключення до БД і оновлення IsDbConnected.
        /// </summary>
        public async Task CheckDbAsync()
        {
            bool ok;
            try
            {
                ok = await _dbService.CanConnectAsync();
            }
            catch
            {
                ok = false;
            }

            _vm.Database.IsDbConnected = ok;
        }

        /// <summary>
        /// Відміняє поточний DB-запит (якщо він виконується).
        /// Використовуй при зміні вкладки.
        /// </summary>
        public void CancelCurrentLoad()
        {
            try { _cts?.Cancel(); } catch { }
        }

        /// <summary>
        /// Debounce-планування перезавантаження DB при зміні дати/фільтра.
        /// </summary>
        public void ScheduleReload()
        {
            _reloadTimer.Stop();

            if (!IsDbTabSelected)
                return;

            if (!_vm.Database.IsDbConnected)
                return;

            _reloadTimer.Start();
        }

        /// <summary>
        /// Завантажує дані для поточної DB-вкладки.
        /// Якщо force=false — не вантажимо, якщо запит (дата+фільтр) не змінився.
        /// </summary>
        public async Task LoadCurrentTabAsync(bool force)
        {
            if (!_vm.Database.IsDbConnected)
                return;

            if (!IsDbTabSelected)
                return;

            var current = new DbQueryKey(DbDate, DbFilter);

            switch (SelectedMainTab)
            {
                case MainTabKind.OperationActions:
                    if (!force && _lastOpActsQuery.HasValue && _lastOpActsQuery.Value.Equals(current))
                        return;

                    await LoadOperatorActsAsync(current);
                    _lastOpActsQuery = current;
                    break;

                case MainTabKind.AlarmHistory:
                    if (!force && _lastAlarmQuery.HasValue && _lastAlarmQuery.Value.Equals(current))
                        return;

                    await LoadAlarmHistoryAsync(current);
                    _lastAlarmQuery = current;
                    break;

                default:
                    return;
            }
        }

        /// <summary>
        /// DB: Operation actions.
        /// </summary>
        private async Task LoadOperatorActsAsync(DbQueryKey key)
        {
            await _gate.WaitAsync();
            CancellationTokenSource? myCts = null;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();

                myCts = new CancellationTokenSource();
                _cts = myCts;

                var ct = myCts.Token;

                _vm.Database.IsDbLoading = true;
                _vm.Shell.BottomText = "Loading DB (Operator actions)...";

                var rows = await _dbService.GetOperatorActsAsync(key.Date, key.Filter, ct);

                await _dispatcher.InvokeAsync(() =>
                {
                    _vm.Database.OperatorActRows.Clear();
                    foreach (var r in rows)
                        _vm.Database.OperatorActRows.Add(r);
                });

                _vm.Shell.BottomText = $"DB Operator actions: {rows.Count}";
            }
            catch (OperationCanceledException)
            {
                _vm.Shell.BottomText = "DB cancelled";
            }
            catch (Exception ex)
            {
                _vm.Shell.BottomText = $"DB Error: {ex.Message}";
            }
            finally
            {
                _vm.Database.IsDbLoading = false;

                if (ReferenceEquals(_cts, myCts))
                {
                    _cts?.Dispose();
                    _cts = null;
                }

                _gate.Release();
            }
        }

        /// <summary>
        /// DB: Alarm history.
        /// </summary>
        private async Task LoadAlarmHistoryAsync(DbQueryKey key)
        {
            await _gate.WaitAsync();
            CancellationTokenSource? myCts = null;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();

                myCts = new CancellationTokenSource();
                _cts = myCts;

                var ct = myCts.Token;

                _vm.Database.IsDbLoading = true;
                _vm.Shell.BottomText = "Loading DB (Alarm history)...";

                var rows = await _dbService.GetAlarmHistoryAsync(key.Date, key.Filter, ct);

                await _dispatcher.InvokeAsync(() =>
                {
                    _vm.Database.AlarmHistoryRows.Clear();
                    foreach (var r in rows)
                        _vm.Database.AlarmHistoryRows.Add(r);
                });

                _vm.Shell.BottomText = $"DB Alarm history: {rows.Count}";
            }
            catch (OperationCanceledException)
            {
                _vm.Shell.BottomText = "DB cancelled";
            }
            catch (Exception ex)
            {
                _vm.Shell.BottomText = $"DB Error: {ex.Message}";
            }
            finally
            {
                _vm.Database.IsDbLoading = false;

                if (ReferenceEquals(_cts, myCts))
                {
                    _cts?.Dispose();
                    _cts = null;
                }

                _gate.Release();
            }
        }
    }
}
