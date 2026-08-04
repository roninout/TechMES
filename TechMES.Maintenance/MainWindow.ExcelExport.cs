using System.IO;
using System.Windows;
using Microsoft.Win32;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    private readonly InfoExportPackageService _infoExportPackageService = new();
    private string _excelExportFilePath = "";
    private string _exportExcelStatusText = "Excel export has not been started.";
    private bool _isExcelExportRunning;

    /// <summary>
    /// Полный путь к выходному XLSX-файлу.
    /// </summary>
    public string ExcelExportFilePath
    {
        get => _excelExportFilePath;
        set
        {
            if (_excelExportFilePath == value)
                return;

            _excelExportFilePath = value;
            _configuration.ImportEdit.ExcelExportFilePath = value;

            OnPropertyChanged(nameof(ExcelExportFilePath));
        }
    }

    /// <summary>
    /// Статус последней операции Excel Export.
    /// </summary>
    public string ExportExcelStatusText
    {
        get => _exportExcelStatusText;
        set
        {
            if (_exportExcelStatusText == value)
                return;

            _exportExcelStatusText = value;

            OnPropertyChanged(nameof(ExportExcelStatusText));
        }
    }

    /// <summary>
    /// Показывает, что экспорт уже выполняется.
    /// </summary>
    public bool IsExcelExportRunning
    {
        get => _isExcelExportRunning;
        private set
        {
            if (_isExcelExportRunning == value)
                return;

            _isExcelExportRunning = value;

            OnPropertyChanged(nameof(IsExcelExportRunning));
            OnPropertyChanged(nameof(CanStartExcelExport));
        }
    }

    /// <summary>
    /// Разрешает запуск только одной операции Export одновременно.
    /// </summary>
    public bool CanStartExcelExport => !IsExcelExportRunning;

    /// <summary>
    /// Выбирает путь выходного XLSX-файла.
    /// </summary>
    private void OnBrowseExcelExportFileClick(object sender, RoutedEventArgs e)
    {
        var dialog =
            new SaveFileDialog
            {
                Title = "Select Excel export file",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|" + "All files (*.*)|*.*",
                AddExtension = true,
                DefaultExt = ".xlsx",
                CheckPathExists = true,
                
                // Подтверждение выполняем перед экспортом всего пакета, а не только одного XLSX-файла.                
                OverwritePrompt = false,
                FileName = "TechMES_Info_Export.xlsx"
            };

        if (!string.IsNullOrWhiteSpace(ExcelExportFilePath))
        {
            try
            {
                var fullPath = Path.GetFullPath(ExcelExportFilePath);
                var directory = Path.GetDirectoryName(fullPath);

                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    dialog.InitialDirectory = directory;

                var fileName = Path.GetFileName(fullPath);

                if (!string.IsNullOrWhiteSpace(fileName))
                    dialog.FileName = fileName;
            }
            catch
            {
                // Старый некорректный путь не блокирует выбор нового.
            }
        }

        if (dialog.ShowDialog(this) != true)
            return;

        ExcelExportFilePath = dialog.FileName;
        PersistImportEditOptions();
        ExportExcelStatusText = $"Export file selected: " + $"{Path.GetFileName(dialog.FileName)}";
    }

    /// <summary>
    /// Создаёт полный переносимый экспортный пакет.
    /// </summary>
    private async void OnExportExcelClick(object sender, RoutedEventArgs e)
    {
        if (IsExcelExportRunning)
            return;

        if (string.IsNullOrWhiteSpace(ExcelExportFilePath))
        {
            ExportExcelStatusText = "Select an Excel export file first.";
            MessageBox.Show(this, ExportExcelStatusText, "EXPORT", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(ExcelExportFilePath.Trim().Trim('"'));

            if (!string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Export file must have the .xlsx extension.");
            }
        }
        catch (Exception ex)
        {
            ExportExcelStatusText = $"Invalid export path: {ex.Message}";
            MessageBox.Show(this, ExportExcelStatusText, "EXPORT", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        if (_infoExportPackageService.PackageExists(fullPath))
        {
            var confirmation = MessageBox.Show(this,
                                    "The selected export package already exists." +
                                    Environment.NewLine +
                                    Environment.NewLine +
                                    "The XLSX file and the folders Supplier_logo, " +
                                    "Instruction and Scheme will be replaced." +
                                    Environment.NewLine +
                                    Environment.NewLine +
                                    "Continue?",
                                    "Replace export package",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;
        }

        IsExcelExportRunning = true;

        try
        {
            ExcelExportFilePath = fullPath;
            PersistImportEditOptions();
            RuntimeCatalogSnapshot? runtimeCatalog = _importRuntimeCatalog;

            /*
             * Station и Type не находятся в Info-БД.
             * Пробуем дополнить экспорт каталогом Runtime,
             * но недоступный Runtime не блокирует экспорт.
             */
            if (runtimeCatalog is null)
            {
                try
                {
                    ExportExcelStatusText = "Loading Runtime catalog for Station and Type...";
                    runtimeCatalog = await _runtimeCatalogClient.LoadEquipmentCatalogAsync(GetRuntimeBaseUrlForImport());
                    _importRuntimeCatalog = runtimeCatalog;
                }
                catch (Exception ex)
                {
                    AppendDiagnostics("Excel export Runtime catalog load failed: " + ex.Message);
                    runtimeCatalog = null;
                }
            }

            ExportExcelStatusText = "Reading Info database and creating export package...";
            var result = await _infoExportPackageService.ExportAsync(GetRuntimeDatabaseConnectionString(), fullPath, runtimeCatalog);
            PersistImportEditOptions();

            var summary =
                "Export completed." +
                Environment.NewLine +
                Environment.NewLine +
                $"SUPPLIER: {result.SupplierCount}" +
                Environment.NewLine +
                $"ORDERS: {result.OrderCount}" +
                Environment.NewLine +
                $"INSTRUCTION: {result.InstructionCount}" +
                Environment.NewLine +
                $"SCHEME: {result.SchemeCount}" +
                Environment.NewLine +
                $"Binary files: {result.BinaryFileCount}" +
                Environment.NewLine +
                Environment.NewLine +
                $"File: {result.ExcelFilePath}";

            if (result.Warnings.Count > 0)
            {
                summary +=
                    Environment.NewLine +
                    Environment.NewLine +
                    "Warnings:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, result.Warnings.Take(10).Select(warning => $"• {warning}"));

                if (result.Warnings.Count > 10)
                {
                    summary += Environment.NewLine + $"• ...and " + $"{result.Warnings.Count - 10} more.";
                }
            }

            ExportExcelStatusText =
                $"Export completed: SUPPLIER {result.SupplierCount}, " +
                $"ORDERS {result.OrderCount}, " +
                $"INSTRUCTION {result.InstructionCount}, " +
                $"SCHEME {result.SchemeCount}.";

            AppendDiagnostics(ExportExcelStatusText);
            MessageBox.Show(this, summary, "EXPORT", MessageBoxButton.OK, result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ExportExcelStatusText = $"Excel export failed: {ex.Message}";
            AppendDiagnostics(ExportExcelStatusText);
            MessageBox.Show(this, ExportExcelStatusText, "EXPORT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsExcelExportRunning = false;
        }
    }
}