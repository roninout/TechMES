using System.Windows;
using System.Windows.Controls;

namespace TechMES.Maintenance.Views;

/// <summary>Вкладка получает модель из TypedAppSettings и использует общие Reload/Save.</summary>
public partial class RedundancySettingsControl : UserControl
{
    /// <summary>Создаёт элементы управления новой вкладки.</summary>
    public RedundancySettingsControl() => InitializeComponent();

    /// <summary>Передаёт проверку главному окну, которое знает рабочий адрес Runtime.</summary>
    private async void OnCheckAccessClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            await window.CheckPostgreSqlControlAccessAsync();
    }
}