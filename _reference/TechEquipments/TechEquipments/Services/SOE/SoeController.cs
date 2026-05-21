using DevExpress.Xpf.Core;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TechEquipments.ViewModels;
using static TechEquipments.IEquipmentService;

namespace TechEquipments
{
    /// <summary>
    /// Вынесенная логика загрузки SOE:
    /// - отмена предыдущей загрузки
    /// - gate от параллельных загрузок
    /// - overlay/progress обновление
    /// - запись результата в rows target
    /// </summary>
    public sealed class SoeController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly ShellViewModel _shell;
        private readonly ObservableCollection<EquipmentSOEDto> _rows;
        private readonly Dispatcher _dispatcher;
        private readonly Func<Window> _getOwnerWindow;

        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private CancellationTokenSource? _loadCts;

        public SoeController(
            IEquipmentService equipmentService,
            ShellViewModel shell,
            ObservableCollection<EquipmentSOEDto> rows,
            Dispatcher dispatcher,
            Func<Window> getOwnerWindow)
        {
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _getOwnerWindow = getOwnerWindow ?? throw new ArgumentNullException(nameof(getOwnerWindow));
        }

        /// <summary>
        /// Загрузка SOE по выбранному оборудованию.
        /// </summary>
        public async Task LoadAndShowAsync(string equipName)
        {
            var name = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            try { _loadCts?.Cancel(); } catch { }

            await _loadGate.WaitAsync();

            CancellationTokenSource? myCts = null;

            try
            {
                _loadCts?.Dispose();
                myCts = new CancellationTokenSource();
                _loadCts = myCts;
                var ct = myCts.Token;

                await _dispatcher.InvokeAsync(() =>
                {
                    _shell.IsLoading = true;
                    _shell.LoadedCount = 0;
                    _shell.CurrentCount = 0;
                    _shell.CurrentTrendIndex = 0;
                    _shell.CurrentTrendName = "";
                    _shell.TotalTrends = 0;
                }, DispatcherPriority.Render);

                var progress = new Progress<LoadingProgress>(p =>
                {
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        _shell.TotalTrends = p.TotalTrends;
                        _shell.CurrentTrendIndex = p.CurrentTrendIndex;
                        _shell.CurrentTrendName = p.CurrentTrendName;
                        _shell.CurrentCount = p.CurrentTrendCount;
                        _shell.LoadedCount = p.TotalLoaded;
                    }), DispatcherPriority.Background);
                });

                var rows = await _equipmentService.GetDataFromEquipAsync(
                    name,
                    progress,
                    ct,
                    perTrendMax: 1000,
                    totalMax: 100);

                ct.ThrowIfCancellationRequested();

                await _dispatcher.InvokeAsync(() =>
                {
                    _rows.Clear();
                    foreach (var r in rows)
                        _rows.Add(r);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                await _dispatcher.InvokeAsync(() => _shell.CurrentTrendName = "Cancelled");
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(_getOwnerWindow(), ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_loadCts, myCts))
                {
                    await _dispatcher.InvokeAsync(() => _shell.IsLoading = false, DispatcherPriority.Render);

                    _loadCts?.Dispose();
                    _loadCts = null;
                }

                _loadGate.Release();
            }
        }

        /// <summary>Отмена текущей загрузки (кнопка Cancel).</summary>
        public void Cancel()
        {
            try { _loadCts?.Cancel(); } catch { }
        }
    }
}
