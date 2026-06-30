using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    private string _supplierLogoFilter = "";
    private string _supplierNameFilter = "";
    private string _supplierStatusFilter = "";

    /// <summary>
    /// Lazy-loads Import/Edit tabs. Supplier and Orders read PostgreSQL directly;
    /// Instruction and Scheme first require Runtime catalog data.
    /// </summary>
    private async void OnImportTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;

        if (sender is not TabControl tabControl || tabControl.SelectedItem is not TabItem selectedTab)
            return;

        var tag = selectedTab.Tag?.ToString();
        try
        {
            switch (tag)
            {
                case "SUPPLIER" when ImportSuppliers.Count == 0:
                    await RefreshImportSuppliersAsync();
                    break;

                case "ORDERS" when ImportOrders.Count == 0:
                    await RefreshImportOrdersAsync();
                    break;

                case "INSTRUCTION":
                case "SCHEME":
                    await EnsureRuntimeCatalogForImportAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendDiagnostics($"Import/Edit tab load failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Import/Edit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Reloads supplier rows from public.equip_supplier.
    /// </summary>
    private async void OnRefreshImportSuppliersClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshImportSuppliersAsync();
        }
        catch (Exception ex)
        {
            ImportSupplierStatusText = $"Supplier refresh failed: {ex.Message}";
            AppendDiagnostics(ImportSupplierStatusText);
        }
    }

    /// <summary>
    /// Saves supplier rows. Selected logo files are written to logo_data only after Save.
    /// </summary>
    private async void OnSaveImportSuppliersClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var activeSuppliers = ImportSuppliers
                .Where(row => !row.IsPendingDelete)
                .ToList();
            var pendingDelete = GetPendingDeletedSupplierNames();

            var saved = await _infoImportEditStore.SaveSuppliersAsync(
                GetRuntimeDatabaseConnectionString(),
                activeSuppliers);
            var deleted = await _infoImportEditStore.DeleteSuppliersAsync(
                GetRuntimeDatabaseConnectionString(),
                pendingDelete);

            ImportSupplierStatusText = deleted > 0
                ? $"Supplier rows saved: {saved}; deleted: {deleted}."
                : $"Supplier rows saved: {saved}.";
            AppendDiagnostics(ImportSupplierStatusText);
            await RefreshImportSuppliersAsync();
        }
        catch (Exception ex)
        {
            ImportSupplierStatusText = $"Supplier save failed: {ex.Message}";
            AppendDiagnostics(ImportSupplierStatusText);
            MessageBox.Show(this, ImportSupplierStatusText, "SUPPLIER", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Selects a supplier logo image and keeps it in memory until Save.
    /// </summary>
    private void OnChooseSupplierLogoClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ImportSupplierRowViewModel row)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Choose supplier logo",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        row.PendingLogoData = File.ReadAllBytes(dialog.FileName);
        row.SetLogoPreview(row.PendingLogoData);
        row.LogoFileName = Path.GetFileName(dialog.FileName);
        row.LogoChanged = true;
        row.LogoStatus = "Selected";
        ImportSupplierStatusText = $"Logo selected for {row.Supplier}: {row.LogoFileName}";
        CollectionViewSource.GetDefaultView(ImportSuppliers).Refresh();
    }

    /// <summary>
    /// Reloads order rows from public.equip_order and public.equip_supplier.
    /// </summary>
    private async void OnRefreshImportOrdersClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshImportOrdersAsync();
        }
        catch (Exception ex)
        {
            ImportOrderStatusText = $"Orders refresh failed: {ex.Message}";
            AppendDiagnostics(ImportOrderStatusText);
        }
    }

    /// <summary>
    /// Saves order rows. Supplier names are resolved to supplier_id in the store layer.
    /// </summary>
    private async void OnSaveImportOrdersClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = await _infoImportEditStore.SaveOrdersAsync(
                GetRuntimeDatabaseConnectionString(),
                ImportOrders);

            PersistImportEditOptions();
            ImportOrderStatusText = $"Order rows saved: {saved}.";
            AppendDiagnostics(ImportOrderStatusText);
            await RefreshImportOrdersAsync();
        }
        catch (Exception ex)
        {
            ImportOrderStatusText = $"Orders save failed: {ex.Message}";
            AppendDiagnostics(ImportOrderStatusText);
            MessageBox.Show(this, ImportOrderStatusText, "ORDERS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Chooses the source folder where new ORDERS PDF files will be taken from.
    /// </summary>
    private void OnBrowseOrdersPdfSourceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose ORDERS PDF source folder",
            Multiselect = false
        };

        if (Directory.Exists(OrdersPdfSourceRoot))
            dialog.InitialDirectory = OrdersPdfSourceRoot;

        if (dialog.ShowDialog(this) != true)
            return;

        OrdersPdfSourceRoot = dialog.FolderName;
        PersistImportEditOptions();
        ImportOrderStatusText = $"PDF source folder saved: {OrdersPdfSourceRoot}";
    }

    /// <summary>
    /// Persists the ORDERS PDF source folder after manual path editing.
    /// </summary>
    private void OnOrdersPdfSourceLostFocus(object sender, RoutedEventArgs e)
    {
        PersistImportEditOptions();
    }

    /// <summary>
    /// Handles Ctrl+V in Import/Edit tables and pastes tabular data from Excel-like sources.
    /// </summary>
    private void OnImportGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        if (e.Key == Key.Delete
            && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is not TextBox
            && TryDeleteSelectedImportRows(grid))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || !Clipboard.ContainsText())
            return;

        PasteClipboardIntoImportGrid(grid, Clipboard.GetText());
        e.Handled = true;
    }

    /// <summary>
    /// Обновляет фильтр SUPPLIER по текстовым полям в заголовках колонок.
    /// Фильтр не меняет данные, а только скрывает строки в текущем представлении таблицы.
    /// </summary>
    private void OnSupplierFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        switch (textBox.Tag?.ToString())
        {
            case "Logo":
                _supplierLogoFilter = textBox.Text;
                break;

            case "Supplier":
                _supplierNameFilter = textBox.Text;
                break;

            case "Status":
                _supplierStatusFilter = textBox.Text;
                break;
        }

        ApplyImportSupplierFilter();
    }

    /// <summary>
    /// Reads suppliers from PostgreSQL and replaces the editable UI collection.
    /// </summary>
    private async Task RefreshImportSuppliersAsync()
    {
        ImportSupplierStatusText = "Loading suppliers...";
        var rows = await _infoImportEditStore.LoadSuppliersAsync(GetRuntimeDatabaseConnectionString());

        ImportSuppliers.Clear();
        foreach (var row in rows)
            ImportSuppliers.Add(row);

        ApplyImportSupplierFilter();
        ImportSupplierStatusText = $"Supplier rows loaded: {ImportSuppliers.Count}.";
    }

    /// <summary>
    /// Подключает фильтр к CollectionView, который WPF строит поверх коллекции ImportSuppliers.
    /// </summary>
    private void ApplyImportSupplierFilter()
    {
        var view = CollectionViewSource.GetDefaultView(ImportSuppliers);
        view.Filter = FilterImportSupplierRow;
        view.Refresh();
    }

    /// <summary>
    /// Проверяет одну строку SUPPLIER по всем активным фильтрам заголовков.
    /// </summary>
    private bool FilterImportSupplierRow(object item)
    {
        if (item == CollectionView.NewItemPlaceholder)
            return true;

        return item is ImportSupplierRowViewModel row
            && ContainsFilter(row.LogoFileName, _supplierLogoFilter)
            && ContainsFilter(row.Supplier, _supplierNameFilter)
            && ContainsFilter(row.LogoStatus, _supplierStatusFilter);
    }

    /// <summary>
    /// Выполняет нечувствительную к регистру проверку подстроки для фильтров в заголовках.
    /// </summary>
    private static bool ContainsFilter(string? value, string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || (value ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads orders from PostgreSQL and replaces the editable UI collection.
    /// </summary>
    private async Task RefreshImportOrdersAsync()
    {
        ImportOrderStatusText = "Loading orders...";
        var rows = await _infoImportEditStore.LoadOrdersAsync(GetRuntimeDatabaseConnectionString());

        ImportOrders.Clear();
        foreach (var row in rows)
            ImportOrders.Add(row);

        ImportOrderStatusText = $"Order rows loaded: {ImportOrders.Count}.";
    }

    /// <summary>
    /// Loads Runtime catalog for future Instruction and Scheme tabs.
    /// A stopped Runtime is a normal operator error, so it is shown as a clear modal message.
    /// </summary>
    private async Task EnsureRuntimeCatalogForImportAsync()
    {
        try
        {
            ImportRuntimeStatusText = "Loading Runtime catalog...";
            _importRuntimeCatalog = await _runtimeCatalogClient.LoadEquipmentCatalogAsync(GetRuntimeBaseUrlForImport());
            ImportRuntimeStatusText =
                $"Runtime catalog loaded: stations {_importRuntimeCatalog.Stations.Count}, types {_importRuntimeCatalog.Types.Count}, equipment {_importRuntimeCatalog.Equipments.Count}.";
        }
        catch (Exception ex)
        {
            _importRuntimeCatalog = null;
            ImportRuntimeStatusText = $"Runtime catalog load failed: {ex.Message}";
            MessageBox.Show(
                this,
                "Runtime Service is required for Instruction and Scheme import. Start Runtime Service and try again.",
                "Runtime required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Returns the Runtime main database connection string used by Info module tables.
    /// </summary>
    private string GetRuntimeDatabaseConnectionString()
    {
        if (string.IsNullOrWhiteSpace(TypedAppSettings.RuntimeDatabaseConnectionString))
            throw new InvalidOperationException("Runtime Database connection string is empty. Reload Runtime/Web appsettings first.");

        return TypedAppSettings.RuntimeDatabaseConnectionString;
    }

    /// <summary>
    /// Returns Runtime base URL from WEB appsettings, with health URL fallback for older profiles.
    /// </summary>
    private string GetRuntimeBaseUrlForImport()
    {
        if (!string.IsNullOrWhiteSpace(TypedAppSettings.WebRuntimeBaseUrl))
            return TypedAppSettings.WebRuntimeBaseUrl;

        var runtimeHealthUrl = RuntimeHealthUrl;
        const string suffix = "/api/health";
        if (!string.IsNullOrWhiteSpace(runtimeHealthUrl)
            && runtimeHealthUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return runtimeHealthUrl[..^suffix.Length];
        }

        return "http://localhost:5101/";
    }

    /// <summary>
    /// Saves only Maintenance Import/Edit options, not Runtime/Web appsettings.
    /// </summary>
    private void PersistImportEditOptions()
    {
        _configuration.ImportEdit.OrdersPdfSourceRoot = OrdersPdfSourceRoot;
        _configurationStore.Save(_configuration);
    }

    /// <summary>
    /// Pastes tab-separated data into DataGridTextColumn bindings.
    /// This gives the operator the same quick workflow as Excel: copy a range, select a start cell, paste.
    /// </summary>
    private static void PasteClipboardIntoImportGrid(DataGrid grid, string clipboardText)
    {
        if (grid.ItemsSource is not IList targetList)
            return;

        var columns = grid.Columns
            .OfType<DataGridTextColumn>()
            .Where(x => !x.IsReadOnly)
            .Select(x => new ImportPasteColumn(x, GetBindingPath(x)))
            .Where(x => !string.IsNullOrWhiteSpace(x.PropertyName))
            .OrderBy(x => x.Column.DisplayIndex)
            .ToList();

        if (columns.Count == 0)
            return;

        var selectedCell = grid.SelectedCells.FirstOrDefault();
        var startRow = selectedCell.Item is null || selectedCell.Item == CollectionView.NewItemPlaceholder
            ? Math.Max(0, grid.Items.Count - 1)
            : grid.Items.IndexOf(selectedCell.Item);

        if (startRow < 0)
            startRow = Math.Max(0, grid.Items.Count - 1);

        var selectedColumn = selectedCell.Column as DataGridTextColumn;
        var startColumn = selectedColumn is null
            ? 0
            : Math.Max(0, columns.FindIndex(x => ReferenceEquals(x.Column, selectedColumn)));

        if (startColumn < 0)
            startColumn = 0;

        var lines = clipboardText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n')
            .Split('\n');

        var pasteRowOffset = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = line.TrimEnd('\r').Split('\t');
            if (pasteRowOffset == 0 && LooksLikeHeaderRow(cells, columns))
                continue;

            var item = EnsurePasteRow(targetList, startRow + pasteRowOffset);
            if (item is null)
                break;

            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                var columnIndex = startColumn + cellIndex;
                if (columnIndex >= columns.Count)
                    break;

                var property = item.GetType().GetProperty(columns[columnIndex].PropertyName);
                if (property?.CanWrite == true && property.PropertyType == typeof(string))
                    property.SetValue(item, cells[cellIndex].Trim());
            }

            pasteRowOffset++;
        }

        grid.Items.Refresh();
    }

    /// <summary>
    /// Удаляет выделенные строки Import/Edit по клавише Delete.
    /// Если фокус внутри TextBox, удаление символов остается штатным поведением редактора.
    /// </summary>
    private bool TryDeleteSelectedImportRows(DataGrid grid)
    {
        if (grid.ItemsSource is not IList targetList)
            return false;

        var rows = grid.SelectedItems
            .Cast<object>()
            .Concat(grid.SelectedCells.Select(cell => cell.Item))
            .Where(x => x != CollectionView.NewItemPlaceholder)
            .Where(x => x is not null)
            .Distinct()
            .ToList();

        if (rows.Count == 0)
            return false;

        var supplierRows = rows
            .OfType<ImportSupplierRowViewModel>()
            .ToList();

        if (supplierRows.Count > 0)
        {
            foreach (var row in supplierRows)
                row.IsPendingDelete = true;

            ImportSupplierStatusText =
                $"Supplier rows pending delete: {GetPendingDeletedSupplierNames().Count}. Press Save to write changes; Refresh cancels pending delete.";
            CollectionViewSource.GetDefaultView(ImportSuppliers).Refresh();
            return true;
        }

        foreach (var row in rows)
            targetList.Remove(row);

        CollectionViewSource.GetDefaultView(targetList).Refresh();
        return true;
    }

    /// <summary>
    /// Возвращает только те SUPPLIER-имена, которые были удалены из таблицы и не появились в ней снова до Save.
    /// </summary>
    private IReadOnlyList<string> GetPendingDeletedSupplierNames()
    {
        var activeNames = ImportSuppliers
            .Where(row => !row.IsPendingDelete)
            .Select(row => row.Supplier.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ImportSuppliers
            .Where(row => row.IsPendingDelete)
            .Select(row => row.Supplier.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !activeNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Extracts a simple property path from a DataGridTextColumn binding.
    /// </summary>
    private static string GetBindingPath(DataGridTextColumn column)
    {
        return column.Binding is Binding binding
            ? binding.Path?.Path ?? ""
            : "";
    }

    /// <summary>
    /// Adds new empty rows when pasted data is longer than the current table.
    /// </summary>
    private static object? EnsurePasteRow(IList targetList, int rowIndex)
    {
        while (rowIndex >= targetList.Count)
        {
            switch (targetList)
            {
                case ObservableCollection<ImportSupplierRowViewModel> suppliers:
                    suppliers.Add(new ImportSupplierRowViewModel());
                    break;

                case ObservableCollection<ImportOrderRowViewModel> orders:
                    orders.Add(new ImportOrderRowViewModel());
                    break;

                default:
                    return null;
            }
        }

        return targetList[rowIndex];
    }

    /// <summary>
    /// Skips pasted Excel headers such as Supplier/Product code when the operator copies the whole sheet.
    /// </summary>
    private static bool LooksLikeHeaderRow(IReadOnlyList<string> cells, IReadOnlyList<ImportPasteColumn> columns)
    {
        if (cells.Count == 0)
            return false;

        var matched = 0;
        for (var i = 0; i < Math.Min(cells.Count, columns.Count); i++)
        {
            var normalizedCell = cells[i].Replace(" ", "", StringComparison.OrdinalIgnoreCase);
            var normalizedProperty = columns[i].PropertyName.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
            if (normalizedCell.Equals(normalizedProperty, StringComparison.OrdinalIgnoreCase))
                matched++;
        }

        return matched > 0;
    }

    /// <summary>
    /// Describes a writable paste target column.
    /// </summary>
    private sealed record ImportPasteColumn(DataGridTextColumn Column, string PropertyName);
}
