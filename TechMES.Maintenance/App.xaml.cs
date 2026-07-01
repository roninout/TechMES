using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FilterGrid = FilterDataGrid.FilterDataGrid;

namespace TechMES.Maintenance;

/// <summary>
/// Точка запуска приложения.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Одинаковая высота строк и заголовка таблицы.
    /// </summary>
    private const double TableRowHeight = 36.0;

    /// <summary>
    /// Компактная высота строки внутри popup фильтра.
    /// </summary>
    private const double FilterPopupItemHeight = 28.0;

    public App()
    {
        /*
         * FilterDataGrid загружает собственный Generic.xaml
         * в Resources каждого экземпляра.
         *
         * Поэтому стили заголовка и popup приходится назначать
         * после загрузки каждого FilterDataGrid.
         */
        EventManager.RegisterClassHandler(
            typeof(FilterGrid),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnFilterDataGridLoaded));
    }

    /// <summary>
    /// Настраивает каждый FilterDataGrid после загрузки.
    /// </summary>
    private static void OnFilterDataGridLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FilterGrid grid)
        {
            return;
        }

        /*
         * Выполняем после внутреннего Loaded-обработчика
         * библиотеки FilterDataGrid.
         */
        _ = grid.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                /*
                 * Принудительно назначаем наш темозависимый
                 * стиль заголовков.
                 */
                grid.SetResourceReference(
                    DataGrid.ColumnHeaderStyleProperty,
                    "TechMesFilterDataGridColumnHeaderStyle");

                /*
                 * Фон popup автоматически следует
                 * за светлой и тёмной темой.
                 */
                grid.SetResourceReference(
                    FilterGrid.FilterPopupBackgroundProperty,
                    "SolidBackgroundFillColorBaseBrush");

                /*
                 * Заголовок и строки таблицы имеют
                 * одинаковую высоту.
                 */
                grid.RowHeight = TableRowHeight;
                grid.ColumnHeaderHeight = TableRowHeight;

                /*
                 * Уменьшаем строки со значениями
                 * внутри popup фильтра.
                 */
                ApplyCompactFilterPopupStyles(grid);
            }));
    }

    /// <summary>
    /// Создаёт компактные стили только внутри FilterDataGrid.
    ///
    /// Обычные CheckBox и ListBox приложения
    /// эти изменения не затрагивают.
    /// </summary>
    private static void ApplyCompactFilterPopupStyles(
        FilterGrid grid)
    {
        /*
         * Берём полноценный Fluent-стиль CheckBox от WPF UI
         * и изменяем только размеры.
         */
        var defaultCheckBoxStyle =
            Application.Current.TryFindResource(
                "DefaultCheckBoxStyle") as Style
            ?? Application.Current.TryFindResource(
                typeof(CheckBox)) as Style;

        var compactCheckBoxStyle =
            defaultCheckBoxStyle is null
                ? new Style(typeof(CheckBox))
                : new Style(
                    typeof(CheckBox),
                    defaultCheckBoxStyle);

        compactCheckBoxStyle.Setters.Add(
            new Setter(
                FrameworkElement.MinWidthProperty,
                0.0));

        compactCheckBoxStyle.Setters.Add(
            new Setter(
                FrameworkElement.MinHeightProperty,
                24.0));

        compactCheckBoxStyle.Setters.Add(
            new Setter(
                Control.PaddingProperty,
                new Thickness(4, 1, 4, 1)));

        compactCheckBoxStyle.Setters.Add(
            new Setter(
                Control.FontSizeProperty,
                13.0));

        /*
         * Локальный неявный стиль действует только
         * внутри конкретного FilterDataGrid и его popup.
         */
        grid.Resources[typeof(CheckBox)] =
            compactCheckBoxStyle;

        /*
         * Уменьшаем внешний контейнер каждой строки.
         */
        var defaultListBoxItemStyle =
            Application.Current.TryFindResource(
                "DefaultListBoxItemStyle") as Style
            ?? Application.Current.TryFindResource(
                typeof(ListBoxItem)) as Style;

        var compactListBoxItemStyle =
            defaultListBoxItemStyle is null
                ? new Style(typeof(ListBoxItem))
                : new Style(
                    typeof(ListBoxItem),
                    defaultListBoxItemStyle);

        compactListBoxItemStyle.Setters.Add(
            new Setter(
                FrameworkElement.MinHeightProperty,
                FilterPopupItemHeight));

        compactListBoxItemStyle.Setters.Add(
            new Setter(
                Control.PaddingProperty,
                new Thickness(4, 1, 4, 1)));

        compactListBoxItemStyle.Setters.Add(
            new Setter(
                FrameworkElement.MarginProperty,
                new Thickness(0)));

        grid.Resources[typeof(ListBoxItem)] =
            compactListBoxItemStyle;

        /*
         * Уменьшаем сам квадрат CheckBox и галочку.
         * Эти ресурсы используются динамически
         * стандартным шаблоном WPF UI.
         */
        grid.Resources["CheckBoxHeight"] = 16.0;
        grid.Resources["CheckBoxWidth"] = 16.0;
        grid.Resources["CheckBoxIconSize"] = 10.0;
    }
}