using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    private string _supplierLogoFilter = "";
    private string _supplierNameFilter = "";
    private string _supplierStatusFilter = "";
    private Task<RuntimeCatalogSnapshot>? _importRuntimeCatalogLoadTask;

    /// <summary>
    /// Lazy-loads Import/Edit tabs. Runtime catalog is cached because ORDERS,
    /// INSTRUCTION and SCHEME use the same type/station/equipment dictionaries.
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
                    if (!await EnsureImportOrderLookupDataAsync())
                        break;

                    await RefreshImportOrdersAsync();
                    break;

                case "INSTRUCTION" when ImportInstructions.Count == 0:
                    if (!await EnsureImportDocumentLookupDataAsync())
                        break;

                    await RefreshImportInstructionsAsync();
                    break;

                case "SCHEME" when ImportSchemeFiles.Count == 0 && ImportSchemeLinks.Count == 0:
                    if (!await EnsureImportDocumentLookupDataAsync())
                        break;

                    await RefreshImportSchemesAsync();
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
    /// Handles the second left click before DataGrid processes it.
    /// The file picker opens only when the clicked cell belongs to the Logo column.
    /// </summary>
    private void OnSupplierGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        var cell = FindVisualParent<DataGridCell>(source);
        if (cell is null)
            return;

        if (!string.Equals(
                cell.Column.SortMemberPath,
                nameof(ImportSupplierRowViewModel.LogoFileName),
                StringComparison.Ordinal))
        {
            return;
        }

        if (cell.DataContext is not ImportSupplierRowViewModel row)
            return;

        // Prevent DataGrid from processing the second click as editing/selection input.
        e.Handled = true;
        ChooseSupplierLogo(row);
    }

    /// <summary>
    /// Finds the nearest visual parent of the requested type.
    /// </summary>
    private static T? FindVisualParent<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result)
                return result;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// Selects a supplier logo image and keeps it in memory until Save.
    /// </summary>
    /// <summary>
    /// Выбирает логотип поставщика и сохраняет его в памяти до нажатия Save.
    /// </summary>
    private void ChooseSupplierLogo(ImportSupplierRowViewModel row)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose supplier logo",
            Filter =
                "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|" +
                "*.png;*.jpg;*.jpeg;*.bmp;*.gif|" +
                "All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var logoData = File.ReadAllBytes(dialog.FileName);

        row.PendingLogoData = logoData;
        row.SetLogoPreview(logoData);
        row.LogoFileName = Path.GetFileName(dialog.FileName);
        row.LogoChanged = true;
        row.LogoStatus = "Selected";

        ImportSupplierStatusText =
            $"Logo selected for {row.Supplier}: {row.LogoFileName}";

        /*
         * CollectionView.Refresh() здесь вызывать нельзя:
         * строка таблицы может находиться в AddNew/EditItem transaction.
         *
         * SetLogoPreview и свойства ViewModel сами должны уведомить интерфейс
         * через PropertyChanged.
         */
    }

    /// <summary>
    /// Reloads order rows from public.equip_order and public.equip_supplier.
    /// </summary>
    private async void OnRefreshImportOrdersClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureImportOrderLookupDataAsync())
                return;

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
            if (!await EnsureImportOrderLookupDataAsync())
                return;

            var invalidLookupMessages = GetInvalidImportLookupMessages(ImportOrders);
            if (invalidLookupMessages.Count > 0)
            {
                ImportOrderStatusText = $"Orders contain invalid lookup values: {invalidLookupMessages.Count}.";
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, invalidLookupMessages.Take(12)),
                    "ORDERS lookup validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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
    /// Reloads INSTRUCTION rows and merges existing DB links with the Runtime equipment catalog.
    /// </summary>
    private async void OnRefreshImportInstructionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureImportDocumentLookupDataAsync())
                return;

            await RefreshImportInstructionsAsync();
        }
        catch (Exception ex)
        {
            ImportInstructionStatusText = $"Instruction refresh failed: {ex.Message}";
            AppendDiagnostics(ImportInstructionStatusText);
        }
    }

    /// <summary>
    /// Saves INSTRUCTION source files and equipment links.
    /// </summary>
    private async void OnSaveImportInstructionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureImportDocumentLookupDataAsync())
                return;

            var invalidLookupMessages = GetInvalidImportLookupMessages(ImportInstructions);
            if (invalidLookupMessages.Count > 0)
            {
                ImportInstructionStatusText = $"Instruction contains invalid lookup values: {invalidLookupMessages.Count}.";
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, invalidLookupMessages.Take(12)),
                    "INSTRUCTION lookup validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var saved = await _infoImportEditStore.SaveInstructionsAsync(
                GetRuntimeDatabaseConnectionString(),
                InstructionPdfSourceRoot,
                ImportInstructions);

            PersistImportEditOptions();
            ImportInstructionStatusText = $"Instruction rows saved: {saved}.";
            AppendDiagnostics(ImportInstructionStatusText);
            await RefreshImportInstructionsAsync();
        }
        catch (Exception ex)
        {
            ImportInstructionStatusText = $"Instruction save failed: {ex.Message}";
            AppendDiagnostics(ImportInstructionStatusText);
            MessageBox.Show(this, ImportInstructionStatusText, "INSTRUCTION", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Chooses the source folder where new INSTRUCTION PDF files will be taken from.
    /// </summary>
    private void OnBrowseInstructionPdfSourceClick(object sender, RoutedEventArgs e)
    {
        var folder = ChooseImportSourceFolder("Choose INSTRUCTION PDF source folder", InstructionPdfSourceRoot);
        if (folder is null)
            return;

        InstructionPdfSourceRoot = folder;
        PersistImportEditOptions();
        ImportInstructionStatusText = $"PDF source folder saved: {InstructionPdfSourceRoot}";
    }

    /// <summary>
    /// Persists the INSTRUCTION PDF source folder after manual path editing.
    /// </summary>
    private void OnInstructionPdfSourceLostFocus(object sender, RoutedEventArgs e)
    {
        PersistImportEditOptions();
    }

    /// <summary>
    /// Reloads SCHEME file and equipment-link rows.
    /// </summary>
    private async void OnRefreshImportSchemesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureImportDocumentLookupDataAsync())
                return;

            await RefreshImportSchemesAsync();
        }
        catch (Exception ex)
        {
            ImportSchemeStatusText = $"Scheme refresh failed: {ex.Message}";
            AppendDiagnostics(ImportSchemeStatusText);
        }
    }

    /// <summary>
    /// Saves SCHEME files and the equipment-to-scheme link table.
    /// </summary>
    private async void OnSaveImportSchemesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureImportDocumentLookupDataAsync())
                return;

            RefreshImportSchemeSourceOptions();

            var invalidLookupMessages = GetInvalidImportLookupMessages(ImportSchemeFiles)
                .Concat(GetInvalidImportLookupMessages(ImportSchemeLinks))
                .ToList();
            if (invalidLookupMessages.Count > 0)
            {
                ImportSchemeStatusText = $"Scheme contains invalid lookup values: {invalidLookupMessages.Count}.";
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, invalidLookupMessages.Take(12)),
                    "SCHEME lookup validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var saved = await _infoImportEditStore.SaveSchemesAsync(
                GetRuntimeDatabaseConnectionString(),
                SchemePdfSourceRoot,
                ImportSchemeFiles,
                ImportSchemeLinks);

            PersistImportEditOptions();
            ImportSchemeStatusText = $"Scheme rows saved: {saved}.";
            AppendDiagnostics(ImportSchemeStatusText);
            await RefreshImportSchemesAsync();
        }
        catch (Exception ex)
        {
            ImportSchemeStatusText = $"Scheme save failed: {ex.Message}";
            AppendDiagnostics(ImportSchemeStatusText);
            MessageBox.Show(this, ImportSchemeStatusText, "SCHEME", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Chooses the source folder where new SCHEME PDF files will be taken from.
    /// </summary>
    private void OnBrowseSchemePdfSourceClick(object sender, RoutedEventArgs e)
    {
        var folder = ChooseImportSourceFolder("Choose SCHEME PDF source folder", SchemePdfSourceRoot);
        if (folder is null)
            return;

        SchemePdfSourceRoot = folder;
        PersistImportEditOptions();
        ImportSchemeStatusText = $"PDF source folder saved: {SchemePdfSourceRoot}";
    }

    /// <summary>
    /// Persists the SCHEME PDF source folder after manual path editing.
    /// </summary>
    private void OnSchemePdfSourceLostFocus(object sender, RoutedEventArgs e)
    {
        PersistImportEditOptions();
    }

    /// <summary>
    /// Opens a standard Windows folder picker for Import/Edit source folders.
    /// </summary>
    private string? ChooseImportSourceFolder(string title, string currentPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (Directory.Exists(currentPath))
            dialog.InitialDirectory = currentPath;

        return dialog.ShowDialog(this) == true
            ? dialog.FolderName
            : null;
    }

    /// <summary>
    /// Handles Ctrl+V in Import/Edit tables and pastes tabular data from Excel-like sources.
    /// </summary>
    private void OnImportGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        /*
         * FilterDataGrid не всегда корректно формирует Clipboard
         * для DataGridTemplateColumn с ComboBox.
         *
         * Для ProductCode во вкладке INSTRUCTION копируем значение
         * явно из ViewModel. Это работает как в режиме просмотра,
         * так и когда ComboBox уже открыт.
         */
        if (e.Key == Key.C
            && Keyboard.Modifiers == ModifierKeys.Control
            && TryCopyInstructionProductCodes(grid))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete
            && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is not TextBox
            && TryDeleteSelectedImportRows(grid))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.V
            || Keyboard.Modifiers != ModifierKeys.Control
            || !Clipboard.ContainsText())
        {
            return;
        }

        PasteClipboardIntoImportGrid(
            grid,
            Clipboard.GetText());

        e.Handled = true;
    }

    /// <summary>
    /// Копирует выбранные ProductCode из INSTRUCTION как обычный текст.
    ///
    /// Никакие заголовки в Clipboard не добавляются, поэтому значение
    /// можно сразу вставить в Product code другой строки.
    /// При выборе нескольких ячеек коды копируются построчно.
    /// </summary>
    private static bool TryCopyInstructionProductCodes(
        DataGrid grid)
    {
        var selectedProductCodeCells = grid.SelectedCells
            .Where(cell =>
                cell.Item is ImportInstructionRowViewModel
                && string.Equals(
                    GetBindingPath(cell.Column),
                    nameof(ImportInstructionRowViewModel.ProductCode),
                    StringComparison.Ordinal))
            .OrderBy(cell =>
                grid.Items.IndexOf(cell.Item))
            .ToList();

        if (selectedProductCodeCells.Count == 0)
            return false;

        var values = selectedProductCodeCells
            .Select(cell =>
                ((ImportInstructionRowViewModel)cell.Item).ProductCode)
            .ToList();

        Clipboard.SetText(
            string.Join(
                Environment.NewLine,
                values));

        return true;
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
        RefreshImportOrderSupplierOptions();
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
        RefreshImportProductCodeOptions();

        /*
         * Если INSTRUCTION уже открыт, ORDERS мог быть обновлён после него.
         * Перепривязываем SelectedOrder и все зависимые read-only поля.
         */
        RefreshImportInstructionOrderDetails();
    }

    /// <summary>
    /// Rebuilds INSTRUCTION rows from Runtime catalog and overlays existing DB links.
    /// Runtime is the master list here, so every equipment item is visible even without a linked file.
    /// </summary>
    private async Task RefreshImportInstructionsAsync()
    {
        if (_importRuntimeCatalog is null)
            throw new InvalidOperationException("Runtime catalog is not loaded.");

        ImportInstructionStatusText = "Loading instruction links...";

        var existingRows =
            await _infoImportEditStore.LoadInstructionsAsync(
                GetRuntimeDatabaseConnectionString());

        var existingByEquipment = existingRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Equipment))
            .GroupBy(
                row => row.Equipment.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);

        var ordersByProductCode = CreateImportOrdersByProductCode();

        ImportInstructions.Clear();

        foreach (var item in _importRuntimeCatalog.EquipmentItems)
        {
            existingByEquipment.TryGetValue(
                item.Equipment,
                out var existing);

            var productCode =
                existing?.ProductCode?.Trim()
                ?? "";

            ordersByProductCode.TryGetValue(
                productCode,
                out var selectedOrder);

            var row = new ImportInstructionRowViewModel
            {
                Station = item.Station,
                Type = item.Type,
                Equipment = item.Equipment
            };

            /*
             * Supplier, Source, Description и Image всегда берутся
             * из ORDERS по ProductCode. Значения из старой instruction-связи
             * для этих колонок не используются.
             */
            row.ApplyOrder(
                selectedOrder,
                productCode);

            ImportInstructions.Add(row);
        }

        ImportInstructionStatusText =
            $"Instruction rows loaded: {ImportInstructions.Count}.";
    }

    /// <summary>
    /// Создаёт быстрый справочник ORDERS по ProductCode.
    /// Последняя строка с одинаковым кодом имеет приоритет,
    /// что соответствует текущей логике сохранения ORDERS.
    /// </summary>
    private Dictionary<string, ImportOrderRowViewModel>
        CreateImportOrdersByProductCode()
    {
        return ImportOrders
            .Where(order =>
                !string.IsNullOrWhiteSpace(order.ProductCode))
            .GroupBy(
                order => order.ProductCode.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Обновляет ORDERS-данные во всех уже созданных строках INSTRUCTION.
    /// </summary>
    private void RefreshImportInstructionOrderDetails()
    {
        if (ImportInstructions.Count == 0)
            return;

        var ordersByProductCode =
            CreateImportOrdersByProductCode();

        foreach (var row in ImportInstructions)
        {
            var productCode =
                row.ProductCode.Trim();

            ordersByProductCode.TryGetValue(
                productCode,
                out var selectedOrder);

            row.ApplyOrder(
                selectedOrder,
                productCode);
        }
    }

    /// <summary>
    /// Обновляет одну строку INSTRUCTION после вставки ProductCode.
    /// </summary>
    private void RefreshImportInstructionOrderDetails(
        ImportInstructionRowViewModel row)
    {
        var productCode =
            row.ProductCode.Trim();

        var selectedOrder = ImportOrders.FirstOrDefault(
            order => string.Equals(
                order.ProductCode?.Trim(),
                productCode,
                StringComparison.OrdinalIgnoreCase));

        row.ApplyOrder(
            selectedOrder,
            productCode);
    }

    /// <summary>
    /// Loads SCHEME library files and equipment links from PostgreSQL.
    /// Link rows are enriched with Station from the cached Runtime catalog when possible.
    /// </summary>
    private async Task RefreshImportSchemesAsync()
    {
        if (_importRuntimeCatalog is null)
            throw new InvalidOperationException("Runtime catalog is not loaded.");

        ImportSchemeStatusText = "Loading scheme tables...";

        var files = await _infoImportEditStore.LoadSchemeFilesAsync(GetRuntimeDatabaseConnectionString());
        var links = await _infoImportEditStore.LoadSchemeLinksAsync(GetRuntimeDatabaseConnectionString());
        var equipmentByName = _importRuntimeCatalog.EquipmentItems
            .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
            .GroupBy(x => x.Equipment.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        ImportSchemeFiles.Clear();
        foreach (var row in files)
            ImportSchemeFiles.Add(row);

        RefreshImportSchemeSourceOptions();

        ImportSchemeLinks.Clear();
        foreach (var row in links)
        {
            if (equipmentByName.TryGetValue(row.Equipment.Trim(), out var equipment))
            {
                row.Station = equipment.Station;
                if (string.IsNullOrWhiteSpace(row.Type))
                    row.Type = equipment.Type;
            }

            ImportSchemeLinks.Add(row);
        }

        ImportSchemeStatusText =
            $"Scheme files loaded: {ImportSchemeFiles.Count}; links loaded: {ImportSchemeLinks.Count}.";
    }

    /// <summary>
    /// Loads all lookup data required by ORDERS editors.
    /// Suppliers come from PostgreSQL; types come from Runtime catalog.
    /// </summary>
    private async Task<bool> EnsureImportOrderLookupDataAsync()
    {
        if (ImportSuppliers.Count == 0)
            await RefreshImportSuppliersAsync();

        RefreshImportOrderSupplierOptions();

        if (_importRuntimeCatalog is null)
            ImportOrderStatusText = "Loading equipment types from Runtime...";

        return await EnsureRuntimeCatalogForImportAsync();
    }

    /// <summary>
    /// Loads shared lookup data for document tabs: Runtime catalog, ORDERS product codes and SUPPLIER names.
    /// The Runtime task is cached, so switching ORDERS/INSTRUCTION/SCHEME does not create duplicate HTTP calls.
    /// </summary>
    private async Task<bool> EnsureImportDocumentLookupDataAsync()
    {
        if (!await EnsureImportOrderLookupDataAsync())
            return false;

        if (ImportOrders.Count == 0)
            await RefreshImportOrdersAsync();

        RefreshImportProductCodeOptions();
        RefreshImportSchemeSourceOptions();
        return true;
    }

    /// <summary>
    /// Loads Runtime catalog for ORDERS, Instruction and Scheme tabs.
    /// A stopped Runtime is a normal operator error, so it is shown as a clear modal message.
    /// </summary>
    private async Task<bool> EnsureRuntimeCatalogForImportAsync()
    {
        if (_importRuntimeCatalog is not null)
        {
            RefreshImportOrderTypeOptions();
            RefreshImportRuntimeLookupOptions();
            return true;
        }

        IsImportRuntimeCatalogLoading = true;

        try
        {
            ImportRuntimeStatusText = "Loading Runtime catalog...";

            // Даём WPF отрисовать footer и ProgressBar до начала HTTP-запроса.
            await Dispatcher.InvokeAsync(
                static () => { },
                System.Windows.Threading.DispatcherPriority.Render);

            _importRuntimeCatalogLoadTask ??=
                _runtimeCatalogClient.LoadEquipmentCatalogAsync(
                    GetRuntimeBaseUrlForImport());

            _importRuntimeCatalog = await _importRuntimeCatalogLoadTask;

            RefreshImportOrderTypeOptions();
            RefreshImportRuntimeLookupOptions();

            ImportRuntimeStatusText =
                $"Runtime catalog loaded: stations {_importRuntimeCatalog.Stations.Count}, " +
                $"types {_importRuntimeCatalog.Types.Count}, " +
                $"equipment {_importRuntimeCatalog.Equipments.Count}.";

            return true;
        }
        catch (Exception ex)
        {
            _importRuntimeCatalog = null;
            _importRuntimeCatalogLoadTask = null;
            ImportOrderTypeOptions.Clear();
            ImportRuntimeStationOptions.Clear();
            ImportRuntimeTypeOptions.Clear();
            ImportRuntimeEquipmentOptions.Clear();

            ImportRuntimeStatusText =
                $"Runtime catalog load failed: {ex.Message}";

            MessageBox.Show(
                this,
                "Runtime Service is required for ORDERS, Instruction and Scheme import. " +
                "Start Runtime Service and try again.",
                "Runtime required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }
        finally
        {
            IsImportRuntimeCatalogLoading = false;
        }
    }

    /// <summary>
    /// Rebuilds the Supplier combobox options from the current SUPPLIER table rows.
    /// Pending-delete rows are excluded so ORDERS cannot link to suppliers that will be removed.
    /// </summary>
    private void RefreshImportOrderSupplierOptions()
    {
        ReplaceStringOptions(
            ImportOrderSupplierOptions,
            ImportSuppliers
                .Where(row => !row.IsPendingDelete)
                .Select(row => row.Supplier)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rebuilds the Type combobox options from the cached Runtime catalog snapshot.
    /// </summary>
    private void RefreshImportOrderTypeOptions()
    {
        ReplaceStringOptions(
            ImportOrderTypeOptions,
            _importRuntimeCatalog?.Types ?? []);
    }

    /// <summary>
    /// Rebuilds the Runtime lookup dictionaries used by INSTRUCTION and SCHEME combobox columns.
    /// </summary>
    private void RefreshImportRuntimeLookupOptions()
    {
        ReplaceStringOptions(
            ImportRuntimeStationOptions,
            _importRuntimeCatalog?.Stations ?? []);

        ReplaceStringOptions(
            ImportRuntimeTypeOptions,
            _importRuntimeCatalog?.Types ?? []);

        ReplaceStringOptions(
            ImportRuntimeEquipmentOptions,
            _importRuntimeCatalog?.Equipments ?? []);
    }

    /// <summary>
    /// Rebuilds Product code options from the current ORDERS table rows.
    /// </summary>
    private void RefreshImportProductCodeOptions()
    {
        ReplaceStringOptions(
            ImportProductCodeOptions,
            ImportOrders.Select(x => x.ProductCode));
    }

    /// <summary>
    /// Rebuilds the SCHEME link combobox from the current scheme file table.
    /// </summary>
    private void RefreshImportSchemeSourceOptions()
    {
        ReplaceStringOptions(
            ImportSchemeSourceOptions,
            ImportSchemeFiles.Select(x => x.Source));
    }

    /// <summary>
    /// Replaces an observable string list while keeping the same collection instance for XAML bindings.
    /// </summary>
    //private static void ReplaceStringOptions(ObservableCollection<string> target, IEnumerable<string> source)
    //{
    //    target.Clear();
    //    foreach (var value in source)
    //        target.Add(value);
    //}

    private static void ReplaceStringOptions(ObservableCollection<string> target, IEnumerable<string> source)
    {
        var values = source
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /*
         * Нельзя каждый раз выполнять target.Clear().
         *
         * ComboBox привязан к SelectedItem в режиме TwoWay.
         * При очистке ItemsSource WPF сбрасывает выбранное значение
         * и записывает null обратно в ImportOrderRowViewModel.
         */
        if (target.SequenceEqual(
                values,
                StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        target.Clear();

        foreach (var value in values)
        {
            target.Add(value);
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
        _configuration.ImportEdit.InstructionPdfSourceRoot = InstructionPdfSourceRoot;
        _configuration.ImportEdit.SchemePdfSourceRoot = SchemePdfSourceRoot;
        _configurationStore.Save(_configuration);
    }

    /// <summary>
    /// Pastes tab-separated data into writable DataGrid columns.
    /// DataGridTextColumn uses Binding, while template columns use ClipboardContentBinding.
    /// This preserves the Excel workflow after switching SUPPLIER cells to WPF UI templates.
    /// </summary>
    private void PasteClipboardIntoImportGrid(DataGrid grid, string clipboardText)
    {
        if (grid.ItemsSource is not IList targetList)
            return;

        var columns = grid.Columns
            .Where(column => !column.IsReadOnly)
            .Select(column => new ImportPasteColumn(column, GetBindingPath(column)))
            .Where(column => !string.IsNullOrWhiteSpace(column.PropertyName))
            .OrderBy(column => column.Column.DisplayIndex)
            .ToList();

        if (columns.Count == 0)
            return;

        var selectedCell = grid.SelectedCells.FirstOrDefault();

        /*
         * startViewRow относится именно к текущему представлению DataGrid.
         * Это важно после сортировки и фильтрации: индекс в grid.Items
         * может не совпадать с индексом в исходной ObservableCollection.
         */
        var startViewRow =
            selectedCell.Item is null
            || selectedCell.Item == CollectionView.NewItemPlaceholder
                ? Math.Max(0, grid.Items.Count - 1)
                : grid.Items.IndexOf(selectedCell.Item);

        if (startViewRow < 0)
            startViewRow = Math.Max(0, grid.Items.Count - 1);

        var selectedColumn = selectedCell.Column;
        var startColumn = selectedColumn is null
            ? 0
            : Math.Max(0, columns.FindIndex(column => ReferenceEquals(column.Column, selectedColumn)));

        if (startColumn < 0)
            startColumn = 0;

        var lines = clipboardText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n')
            .Split('\n');

        var pasteRowOffset = 0;
        var rejectedLookupCells = 0;
        var orderRowsChanged = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = line.TrimEnd('\r').Split('\t');
            if (pasteRowOffset == 0 && LooksLikeHeaderRow(cells, columns))
                continue;

            var item = GetPasteTargetItem(
                grid,
                targetList,
                startViewRow,
                pasteRowOffset);

            if (item is null)
                break;

            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                var columnIndex = startColumn + cellIndex;
                if (columnIndex >= columns.Count)
                    break;

                var property = item.GetType().GetProperty(columns[columnIndex].PropertyName);
                if (property?.CanWrite == true && property.PropertyType == typeof(string))
                {
                    var value = cells[cellIndex].Trim();
                    var valueToSet = value;
                    if (!TryNormalizeImportLookupValue(item, columns[columnIndex].PropertyName, value, out valueToSet))
                    {
                        rejectedLookupCells++;
                        continue;
                    }

                    property.SetValue(item, valueToSet);

                    if (item is ImportOrderRowViewModel)
                    {
                        /*
                         * После вставки в ORDERS обновим зависимые read-only
                         * поля уже открытой вкладки INSTRUCTION.
                         */
                        orderRowsChanged = true;
                    }

                    /*
                     * ProductCode может быть изменён не только ComboBox,
                     * но и вставкой из Excel. В этом случае вручную
                     * подтягиваем связанные поля из ORDERS.
                     */
                    if (item is ImportInstructionRowViewModel instructionRow
                        && string.Equals(
                            columns[columnIndex].PropertyName,
                            nameof(ImportInstructionRowViewModel.ProductCode),
                            StringComparison.Ordinal))
                    {
                        RefreshImportInstructionOrderDetails(
                            instructionRow);
                    }
                }
            }

            pasteRowOffset++;
        }

        /*
         * Здесь нельзя вызывать grid.Items.Refresh().
         *
         * Ctrl+V обрабатывается в PreviewKeyDown, поэтому DataGrid может
         * находиться внутри EditItem или AddNew transaction. CollectionView
         * запрещает Refresh в этом состоянии и выбрасывает:
         *
         * 'Refresh' is not allowed during an AddNew or EditItem transaction.
         *
         * Все строки Import/Edit наследуются от ObservableObject, а коллекции
         * являются ObservableCollection, поэтому PropertyChanged и CollectionChanged
         * уже автоматически обновляют отображение без ручного Refresh.
         */
        if (orderRowsChanged)
        {
            RefreshImportInstructionOrderDetails();
        }

        if (rejectedLookupCells > 0)
        {
            var message = $"Paste skipped invalid lookup cells: {rejectedLookupCells}. Use values from the combo box dictionaries.";
            SetImportStatusForGrid(grid, message);
            AppendDiagnostics(message);
        }
    }

    /// <summary>
    /// Normalizes pasted values against the combobox dictionaries of the current Import/Edit row type.
    /// Empty values are allowed, but non-empty unknown values are rejected.
    /// </summary>
    private bool TryNormalizeImportLookupValue(object item, string propertyName, string value, out string normalizedValue)
    {
        normalizedValue = value.Trim();

        var options = GetImportLookupOptions(item, propertyName);
        if (options is null || string.IsNullOrWhiteSpace(normalizedValue))
            return true;

        var candidate = normalizedValue;
        var match = options.FirstOrDefault(option =>
            option.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        normalizedValue = match;
        return true;
    }

    /// <summary>
    /// Returns user-facing validation messages for Import/Edit rows that point to missing dictionaries.
    /// </summary>
    private IReadOnlyList<string> GetInvalidImportLookupMessages(IEnumerable rows)
    {
        var messages = new List<string>();
        var index = 0;
        foreach (var item in rows)
        {
            switch (item)
            {
                case ImportOrderRowViewModel row:
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportOrderRowViewModel.Type),
                        row.Type,
                        "Runtime catalog Type");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportOrderRowViewModel.Supplier),
                        row.Supplier,
                        "SUPPLIER table");
                    break;

                case ImportInstructionRowViewModel row:
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportInstructionRowViewModel.Station),
                        row.Station,
                        "Runtime catalog Station");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportInstructionRowViewModel.Type),
                        row.Type,
                        "Runtime catalog Type");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportInstructionRowViewModel.Equipment),
                        row.Equipment,
                        "Runtime catalog Equipment");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportInstructionRowViewModel.ProductCode),
                        row.ProductCode,
                        "ORDERS Product code",
                        isRequired: false);
                    break;

                case ImportSchemeFileRowViewModel row:
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportSchemeFileRowViewModel.Type),
                        row.Type,
                        "Runtime catalog Type");
                    break;

                case ImportSchemeLinkRowViewModel row:
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportSchemeLinkRowViewModel.Station),
                        row.Station,
                        "Runtime catalog Station");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportSchemeLinkRowViewModel.Type),
                        row.Type,
                        "Runtime catalog Type");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportSchemeLinkRowViewModel.Equipment),
                        row.Equipment,
                        "Runtime catalog Equipment");
                    AddImportLookupValidationMessage(
                        messages,
                        index,
                        row,
                        nameof(ImportSchemeLinkRowViewModel.Scheme),
                        row.Scheme,
                        "SCHEME files");
                    break;
            }

            index++;
        }

        return messages;
    }

    /// <summary>
    /// Adds a single lookup validation message when a non-empty value is not present in the dictionary.
    /// </summary>
    private void AddImportLookupValidationMessage(
        ICollection<string> messages,
        int rowIndex,
        object item,
        string propertyName,
        string value,
        string dictionaryName,
        bool isRequired = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (isRequired)
            {
                messages.Add(
                    $"Row {rowIndex + 1}: {propertyName} is required.");
            }

            return;
        }

        var options = GetImportLookupOptions(item, propertyName);

        if (options is null)
        {
            return;
        }

        var normalizedValue = value.Trim();

        if (options.Any(option =>
                option.Equals(
                    normalizedValue,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        messages.Add(
            $"Row {rowIndex + 1}: {propertyName} " +
            $"'{normalizedValue}' is not found in {dictionaryName}.");
    }

    /// <summary>
    /// Maps lookup columns to their current valid option lists.
    /// </summary>
    private IEnumerable<string>? GetImportLookupOptions(object item, string propertyName)
    {
        return item switch
        {
            ImportOrderRowViewModel => propertyName switch
            {
                nameof(ImportOrderRowViewModel.Type) => ImportOrderTypeOptions,
                nameof(ImportOrderRowViewModel.Supplier) => ImportOrderSupplierOptions,
                _ => null
            },
            ImportInstructionRowViewModel => propertyName switch
            {
                nameof(ImportInstructionRowViewModel.Station) => ImportRuntimeStationOptions,
                nameof(ImportInstructionRowViewModel.Type) => ImportRuntimeTypeOptions,
                nameof(ImportInstructionRowViewModel.Equipment) => ImportRuntimeEquipmentOptions,
                nameof(ImportInstructionRowViewModel.ProductCode) => ImportProductCodeOptions,
                _ => null
            },
            ImportSchemeFileRowViewModel => propertyName switch
            {
                nameof(ImportSchemeFileRowViewModel.Type) => ImportRuntimeTypeOptions,
                _ => null
            },
            ImportSchemeLinkRowViewModel => propertyName switch
            {
                nameof(ImportSchemeLinkRowViewModel.Station) => ImportRuntimeStationOptions,
                nameof(ImportSchemeLinkRowViewModel.Type) => ImportRuntimeTypeOptions,
                nameof(ImportSchemeLinkRowViewModel.Equipment) => ImportRuntimeEquipmentOptions,
                nameof(ImportSchemeLinkRowViewModel.Scheme) => ImportSchemeSourceOptions,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>
    /// Writes paste feedback to the status text that belongs to the current Import/Edit table.
    /// </summary>
    private void SetImportStatusForGrid(DataGrid grid, string message)
    {
        switch (grid.ItemsSource)
        {
            case ObservableCollection<ImportSupplierRowViewModel>:
                ImportSupplierStatusText = message;
                break;

            case ObservableCollection<ImportOrderRowViewModel>:
                ImportOrderStatusText = message;
                break;

            case ObservableCollection<ImportInstructionRowViewModel>:
                ImportInstructionStatusText = message;
                break;

            case ObservableCollection<ImportSchemeFileRowViewModel>:
            case ObservableCollection<ImportSchemeLinkRowViewModel>:
                ImportSchemeStatusText = message;
                break;
        }
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
            RefreshImportOrderSupplierOptions();
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
    /// Extracts a writable property path from either a bound column
    /// or a template column with ClipboardContentBinding.
    /// </summary>
    private static string GetBindingPath(DataGridColumn column)
    {
        if (column is DataGridBoundColumn boundColumn
            && boundColumn.Binding is Binding boundBinding)
        {
            return boundBinding.Path?.Path ?? "";
        }

        if (column.ClipboardContentBinding is Binding clipboardBinding)
            return clipboardBinding.Path?.Path ?? "";

        return column.SortMemberPath ?? "";
    }

    /// <summary>
    /// Возвращает строку из текущего представления DataGrid.
    ///
    /// Нельзя использовать индекс grid.Items напрямую для ItemsSource:
    /// после сортировки или фильтрации их порядок может отличаться.
    /// </summary>
    private static object? GetPasteTargetItem(
        DataGrid grid,
        IList targetList,
        int startViewRow,
        int pasteRowOffset)
    {
        var targetViewRow =
            startViewRow + pasteRowOffset;

        if (targetViewRow >= 0
            && targetViewRow < grid.Items.Count)
        {
            var viewItem =
                grid.Items[targetViewRow];

            if (viewItem is not null
                && viewItem != CollectionView.NewItemPlaceholder)
            {
                return viewItem;
            }
        }

        /*
         * За пределами текущего представления сохраняем прежнее
         * поведение: создаём новую строку в исходной коллекции.
         */
        return EnsurePasteRow(
            targetList,
            targetList.Count);
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

                case ObservableCollection<ImportInstructionRowViewModel> instructions:
                    instructions.Add(new ImportInstructionRowViewModel());
                    break;

                case ObservableCollection<ImportSchemeFileRowViewModel> schemeFiles:
                    schemeFiles.Add(new ImportSchemeFileRowViewModel());
                    break;

                case ObservableCollection<ImportSchemeLinkRowViewModel> schemeLinks:
                    schemeLinks.Add(new ImportSchemeLinkRowViewModel());
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
    private sealed record ImportPasteColumn(DataGridColumn Column, string PropertyName);
}
