namespace TechMES.Maintenance.Views;

/// <summary>
/// Страница ручного импорта и редактирования справочников Info-модуля.
/// Основная логика живёт в MainWindow.xaml.cs, а страница только переадресует события через MaintenancePageControl.
/// </summary>
public partial class ImportEditPage : MaintenancePageControl
{
    /// <summary>
    /// Инициализирует XAML-компоненты страницы Import/Edit.
    /// </summary>
    public ImportEditPage()
    {
        InitializeComponent();
    }
}
