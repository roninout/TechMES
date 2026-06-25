namespace TechMES.Maintenance.Views;

/// <summary>
/// Страница Maintenance, вынесенная из общего MainWindow.xaml.
/// Логика остается в MainWindow.xaml.cs, а обработчики событий наследуются от MaintenancePageControl.
/// </summary>
public partial class BackupRestorePage : MaintenancePageControl
{
    /// <summary>
    /// Создает страницу и загружает ее XAML-разметку.
    /// </summary>
    public BackupRestorePage()
    {
        InitializeComponent();
    }
}

