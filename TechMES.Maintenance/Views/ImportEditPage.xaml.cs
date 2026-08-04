using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TechMES.Maintenance.Views;

/// <summary>
/// Страница ручного импорта и редактирования справочников Info-модуля.
/// </summary>
public partial class ImportEditPage : MaintenancePageControl
{
    /*
     * Для каждой таблицы запоминаем подписку на изменение текущего
     * представления Items.
     *
     * CollectionChanged вызывается, в частности, после:
     * - фильтрации;
     * - сортировки;
     * - добавления строки;
     * - удаления строки;
     * - Refresh коллекции.
     */
    private readonly Dictionary<
        DataGrid,
        (
            INotifyCollectionChanged Source,
            NotifyCollectionChangedEventHandler Handler
        )> _rowNumberSubscriptions = new();

    /*
     * Не запускаем несколько одинаковых обновлений нумерации
     * одновременно для одной таблицы.
     */
    private readonly HashSet<DataGrid>
        _pendingRowNumberRefreshes = new();

    /*
     * Начальная вкладка должна выбираться только один раз,
     * после подключения страницы к MainWindow.
     */
    private bool _initialTabSelectionApplied;

    public ImportEditPage()
    {
        InitializeComponent();

        /*
         * IMPORT является общей служебной вкладкой для всех справочников,
         * поэтому в интерфейсе она располагается после SUPPLIER, ORDERS,
         * INSTRUCTION и SCHEME.
         */
        ImportTabControl.Items.Remove(ImportTab);
        ImportTabControl.Items.Add(ImportTab);

        /*
         * Не выбираем SUPPLIER прямо в конструкторе.
         *
         * В этот момент страница ещё не подключена к MainWindow,
         * поэтому SelectionChanged невозможно переадресовать
         * в MainWindow.OnImportTabSelectionChanged.
         *
         * Реальный выбор будет выполнен в OnImportEditPageLoaded.
         */
        ImportTabControl.SelectedIndex = -1;

        Loaded += OnImportEditPageLoaded;
    }

    /// <summary>
    /// Выбирает SUPPLIER после полного подключения страницы к MainWindow.
    ///
    /// Это создаёт настоящее SelectionChanged-событие уже тогда,
    /// когда MaintenancePageControl может переадресовать его в MainWindow.
    /// </summary>
    private void OnImportEditPageLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialTabSelectionApplied)
            return;

        /*
         * Откладываем выбор до завершения текущего Loaded-цикла.
         * К этому времени:
         * - Window.GetWindow(this) уже возвращает MainWindow;
         * - базовый MaintenancePageControl установил DataContext;
         * - визуальное дерево страницы полностью создано.
         */
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (_initialTabSelectionApplied ||
                    !IsLoaded)
                {
                    return;
                }

                _initialTabSelectionApplied = true;

                /*
                 * SUPPLIER теперь выбирается после подключения страницы.
                 * XAML SelectionChanged вызовет загрузку данных
                 * через MainWindow.OnImportTabSelectionChanged.
                 */
                ImportTabControl.SelectedIndex = 0;
            }));
    }

    /// <summary>
    /// Подключает автоматическое обновление номеров строк.
    ///
    /// LoadingRow и Sorting не являются routed events, поэтому их нельзя
    /// подключить через EventSetter в XAML. Подписываемся здесь программно.
    /// </summary>
    private void OnImportGridLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        /*
         * Loaded может вызываться повторно при переключении вкладок.
         * Сначала удаляем возможные старые обработчики, затем добавляем снова.
         * Это предотвращает накопление одинаковых подписок.
         */
        grid.LoadingRow -= OnImportGridLoadingRow;
        grid.LoadingRow += OnImportGridLoadingRow;

        grid.Sorting -= OnImportGridSorting;
        grid.Sorting += OnImportGridSorting;

        /*
         * Если подписка на изменение текущего представления таблицы
         * уже создана, остаётся только обновить номера.
         */
        if (_rowNumberSubscriptions.ContainsKey(grid))
        {
            ScheduleRowNumberRefresh(grid);
            return;
        }

        /*
         * ItemCollection DataGrid сообщает об изменениях после:
         * - фильтрации;
         * - добавления;
         * - удаления;
         * - Refresh текущего представления.
         */
        if (grid.Items is INotifyCollectionChanged source)
        {
            NotifyCollectionChangedEventHandler handler =
                (_, _) => ScheduleRowNumberRefresh(grid);

            source.CollectionChanged += handler;

            _rowNumberSubscriptions.Add(
                grid,
                (source, handler));
        }

        ScheduleRowNumberRefresh(grid);
    }

    /// <summary>
    /// Удаляет все программные подписки при выгрузке таблицы.
    /// </summary>
    private void OnImportGridUnloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        /*
         * Эти события были подключены программно
         * в OnImportGridLoaded.
         */
        grid.LoadingRow -= OnImportGridLoadingRow;
        grid.Sorting -= OnImportGridSorting;

        /*
         * Удаляем подписку на изменение текущего представления Items.
         */
        if (_rowNumberSubscriptions.Remove(
                grid,
                out var subscription))
        {
            subscription.Source.CollectionChanged -=
                subscription.Handler;
        }

        _pendingRowNumberRefreshes.Remove(grid);
    }

    /// <summary>
    /// Присваивает номер каждой новой или повторно созданной строке.
    ///
    /// Этот обработчик также работает при виртуализации:
    /// когда пользователь прокручивает таблицу, WPF создаёт новый
    /// DataGridRow и сразу получает правильный текущий номер.
    /// </summary>
    private void OnImportGridLoadingRow(
        object sender,
        DataGridRowEventArgs e)
    {
        e.Row.Header =
            e.Row.GetIndex() + 1;
    }

    /// <summary>
    /// После сортировки обновляет номера уже отображаемых строк.
    /// </summary>
    private void OnImportGridSorting(
        object sender,
        DataGridSortingEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            ScheduleRowNumberRefresh(grid);
        }
    }

    /// <summary>
    /// Планирует обновление после завершения текущей операции
    /// сортировки, фильтрации или изменения коллекции.
    /// </summary>
    private void ScheduleRowNumberRefresh(
        DataGrid grid)
    {
        if (!_pendingRowNumberRefreshes.Add(grid))
            return;

        _ = grid.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _pendingRowNumberRefreshes.Remove(grid);

                if (!grid.IsLoaded)
                    return;

                RefreshVisibleRowNumbers(grid);
            }));
    }

    /// <summary>
    /// Обновляет номера всех строк, контейнеры которых сейчас
    /// созданы и отображаются в DataGrid.
    ///
    /// Остальные виртуализированные строки получат номер через
    /// OnImportGridLoadingRow при прокрутке.
    /// </summary>
    private static void RefreshVisibleRowNumbers(
        DataGrid grid)
    {
        for (var index = 0;
             index < grid.Items.Count;
             index++)
        {
            if (grid.ItemContainerGenerator
                    .ContainerFromIndex(index)
                is DataGridRow row)
            {
                row.Header =
                    index + 1;
            }
        }
    }

    /// <summary>
    /// Открывает список ComboBox сразу после перехода ячейки
    /// в режим редактирования.
    ///
    /// Первый клик выделяет ячейку/строку.
    /// Второй клик включает редактирование и открывает список.
    /// </summary>
    private void OnOrderLookupComboBoxLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        _ = comboBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
            }));
    }
}