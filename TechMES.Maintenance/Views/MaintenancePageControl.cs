using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TechMES.Maintenance.Views;

/// <summary>
/// Базовый UserControl для страниц Maintenance.
/// Он оставляет существующие обработчики событий в MainWindow.xaml.cs и переадресует туда клики из вынесенных страниц.
/// </summary>
public class MaintenancePageControl : UserControl
{
    private readonly Dictionary<DataGrid, (INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler)> _autoScrollDataGrids = [];

    /// <summary>
    /// Передает обычное RoutedEvent-событие в одноименный метод MainWindow.
    /// </summary>
    protected void ForwardRoutedEvent(object sender, RoutedEventArgs e, [CallerMemberName] string? methodName = null)
    {
        ForwardEvent(methodName, sender, e);
    }

    /// <summary>
    /// Передает SelectionChanged-событие в одноименный метод MainWindow.
    /// </summary>
    protected void ForwardSelectionChangedEvent(object sender, SelectionChangedEventArgs e, [CallerMemberName] string? methodName = null)
    {
        ForwardEvent(methodName, sender, e);
    }

    /// <summary>
    /// Передает keyboard-событие в одноименный метод MainWindow.
    /// Используется таблицами Import/Edit для paste из Excel-подобного буфера.
    /// </summary>
    protected void ForwardKeyEvent(object sender, KeyEventArgs e, [CallerMemberName] string? methodName = null)
    {
        ForwardEvent(methodName, sender, e);
    }

    /// <summary>
    /// Ищет приватный или публичный обработчик на MainWindow и вызывает его с исходными аргументами события.
    /// </summary>
    private void ForwardEvent(string? methodName, object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return;
        }
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }
        var method = window.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is null)
        {
            Debug.WriteLine($"Maintenance handler '{methodName}' was not found on MainWindow.");
            return;
        }
        method.Invoke(window, new object?[] { sender, e });
    }
    public void OnStartServiceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnStopServiceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRestartServiceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnProbeServiceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshAllClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenServicesConsoleClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnPrepareServerClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshServerClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenMaintenanceFolderClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenWebFirewallClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenFirewallAdvancedClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnCreateHttpsCertificateClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenCertificateManagerClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnExportPublicCertificateClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnApplyHttpsConfigClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenHttpsFirewallClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOpenLocalUsersAndGroupsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshServerProfileClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnCreateBackupClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshBackupsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRestoreBackupClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveMaintenanceProfileClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnScanCleanupClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnDeleteCleanupCandidatesClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRunDependencyChecksClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshWindowsAuthDiagnosticsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshOperatorActionDiagnosticsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveDeploymentProfileClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnPublishAllClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnInstallAllClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnStartAllDeployClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnDeployAllClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnPrepareNewServerClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnPublishDeployServiceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnInstallDeployServiceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveTypedSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnReloadTypedRuntimeWebAppSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveTypedRuntimeWebAppSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnReloadAllTypedSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveAllTypedSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSettingsFileSelected(object sender, SelectionChangedEventArgs e) => ForwardSelectionChangedEvent(sender, e);
    public void OnReloadSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveSettingsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshLogsClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnLogFileSelected(object sender, SelectionChangedEventArgs e) => ForwardSelectionChangedEvent(sender, e);
    public void OnLogDateSelected(object sender, SelectionChangedEventArgs e) => ForwardSelectionChangedEvent(sender, e);
    public void OnPreviousLogDateClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnTodayLogDateClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnNextLogDateClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnBackupRootLostFocus(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnBrowseBackupRootClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnExportBackupZipClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnImportBackupZipClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnImportTabSelectionChanged(object sender, SelectionChangedEventArgs e) => ForwardSelectionChangedEvent(sender, e);
    public void OnRefreshImportSuppliersClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveImportSuppliersClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnChooseSupplierLogoClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSupplierFilterTextChanged(object sender, TextChangedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnRefreshImportOrdersClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnSaveImportOrdersClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnBrowseOrdersPdfSourceClick(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnOrdersPdfSourceLostFocus(object sender, RoutedEventArgs e) => ForwardRoutedEvent(sender, e);
    public void OnImportGridPreviewKeyDown(object sender, KeyEventArgs e) => ForwardKeyEvent(sender, e);

    /// <summary>
    /// Прокручивает read-only TextBox с логами вниз при появлении новых строк.
    /// </summary>
    public void OnAutoScrollLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
            textBox.ScrollToEnd();
    }

    /// <summary>
    /// Подписывает DataGrid на изменения коллекции, чтобы диагностические таблицы показывали последнюю строку.
    /// </summary>
    public void OnAutoScrollDataGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.ItemsSource is not INotifyCollectionChanged source)
            return;

        OnAutoScrollDataGridUnloaded(sender, e);

        NotifyCollectionChangedEventHandler handler = (_, _) => ScrollDataGridToLast(grid);
        source.CollectionChanged += handler;
        _autoScrollDataGrids[grid] = (source, handler);
        ScrollDataGridToLast(grid);
    }

    /// <summary>
    /// Отписывает DataGrid от источника при выгрузке страницы.
    /// </summary>
    public void OnAutoScrollDataGridUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || !_autoScrollDataGrids.Remove(grid, out var subscription))
            return;

        subscription.Source.CollectionChanged -= subscription.Handler;
    }

    /// <summary>
    /// Выполняет ScrollIntoView после того, как WPF успел создать визуальную строку.
    /// </summary>
    private static void ScrollDataGridToLast(DataGrid grid)
    {
        grid.Dispatcher.BeginInvoke(() =>
        {
            if (grid.Items.Count > 0)
                grid.ScrollIntoView(grid.Items[^1]);
        });
    }
}
