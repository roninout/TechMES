using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FilterGrid = FilterDataGrid.FilterDataGrid;
using WpfApplication = global::System.Windows.Application;

namespace TechMES.Maintenance;

/// <summary>Точка запуска Maintenance. Явно используем WPF Application.</summary>
public partial class App : WpfApplication
{
    private const double TableRowHeight = 36.0;
    private const double FilterPopupItemHeight = 28.0;

    public App()
    {
        // FilterDataGrid загружает собственные ресурсы, поэтому назначаем стили после Loaded.
        EventManager.RegisterClassHandler(typeof(FilterGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnFilterDataGridLoaded));
    }

    /// <summary>Настраивает каждый FilterDataGrid после загрузки его внутренних ресурсов.</summary>
    private static void OnFilterDataGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FilterGrid grid) return;

        _ = grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // Темозависимые стили заголовков и фона popup.
            grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "TechMesFilterDataGridColumnHeaderStyle");
            grid.SetResourceReference(FilterGrid.FilterPopupBackgroundProperty, "SolidBackgroundFillColorBaseBrush");

            // Сохраняем текущее поведение: высота строк отдельно не фиксируется.
            // grid.RowHeight = TableRowHeight;
            grid.ColumnHeaderHeight = TableRowHeight;

            ApplyCompactFilterPopupStyles(grid);
        }));
    }

    /// <summary>Создаёт компактные стили только внутри FilterDataGrid и его popup.</summary>
    private static void ApplyCompactFilterPopupStyles(FilterGrid grid)
    {
        // Наследуем полноценный Fluent-стиль CheckBox и изменяем только размеры.
        var defaultCheckBoxStyle = WpfApplication.Current.TryFindResource("DefaultCheckBoxStyle") as Style
            ?? WpfApplication.Current.TryFindResource(typeof(CheckBox)) as Style;

        var compactCheckBoxStyle = defaultCheckBoxStyle is null
            ? new Style(typeof(CheckBox))
            : new Style(typeof(CheckBox), defaultCheckBoxStyle);

        compactCheckBoxStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        compactCheckBoxStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 24.0));
        compactCheckBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 1, 4, 1)));
        compactCheckBoxStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));

        grid.Resources[typeof(CheckBox)] = compactCheckBoxStyle;

        // Уменьшаем внешний контейнер строки списка фильтра.
        var defaultListBoxItemStyle = WpfApplication.Current.TryFindResource("DefaultListBoxItemStyle") as Style
            ?? WpfApplication.Current.TryFindResource(typeof(ListBoxItem)) as Style;

        var compactListBoxItemStyle = defaultListBoxItemStyle is null
            ? new Style(typeof(ListBoxItem))
            : new Style(typeof(ListBoxItem), defaultListBoxItemStyle);

        compactListBoxItemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, FilterPopupItemHeight));
        compactListBoxItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 1, 4, 1)));
        compactListBoxItemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));

        grid.Resources[typeof(ListBoxItem)] = compactListBoxItemStyle;

        // Ресурсы размера квадрата CheckBox и галочки используются шаблоном WPF UI.
        grid.Resources["CheckBoxHeight"] = 16.0;
        grid.Resources["CheckBoxWidth"] = 16.0;
        grid.Resources["CheckBoxIconSize"] = 10.0;
    }
}