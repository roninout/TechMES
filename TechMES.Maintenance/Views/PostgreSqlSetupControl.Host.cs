using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSetupControl
{
    /// <summary>Другой режим требует новой проверки контроллера.</summary>
    private void OnHostModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HostStatusBox is not null)
            HostStatusBox.Text = "Режим контроллера изменён. Требуется новая проверка.";
    }

    /// <summary>Выбирает распакованный пакет без изменения установленных служб.</summary>
    private void OnChooseAgentPackageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "Распакованный пакет Agent с manifest.json" };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            SelectedProfile().AgentPackageDirectory = dialog.FolderName;

            if (sender is FrameworkElement { Parent: DockPanel panel })
                foreach (var field in panel.Children.OfType<TextBox>())
                    field.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

            HostStatusBox.Text = "Выбран другой пакет. Требуется новая проверка контроллера.";
        }
        catch (Exception ex)
        {
            HostStatusBox.Text = ex.Message;
        }
    }

    /// <summary>Читает готовность контроллера без изменения служб.</summary>
    private async void OnCheckHostClick(object sender, RoutedEventArgs e) => await RunHostAsync(false);

    /// <summary>Использует общую защиту Maintenance от параллельного deployment.</summary>
    private async void OnPrepareHostClick(object sender, RoutedEventArgs e) => await RunHostAsync(true);

    /// <summary>Показывает фактический результат после завершения автономной консоли.</summary>
    private async Task RunHostAsync(bool prepare)
    {
        try
        {
            SaveForOperation();

            if (Window.GetWindow(this) is MainWindow window)
                HostStatusBox.Text = DescribeHost(await window.RunPostgreSqlHostSetupAsync(SelectedProfile(), prepare));
        }
        catch (Exception ex)
        {
            HostStatusBox.Text = ex.Message;
        }
    }

    /// <summary>Экспортирует инструменты, профиль и выбранный Agent в новый каталог.</summary>
    private async void OnExportHostClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveForOperation();
            var dialog = new OpenFolderDialog { Title = "Каталог комплектов подготовки контроллера" };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true || Window.GetWindow(this) is not MainWindow window)
                return;

            var directory = await window.ExportPostgreSqlHostSetupAsync(SelectedProfile(), dialog.FolderName);
            HostStatusBox.Text = "Комплект: " + directory + "\nСкопируйте весь каталог на " + SelectedProfile().ComputerName + ".\nПроверка: .\\Prepare-Controller.ps1 -Mode Check\nПодготовка: .\\Prepare-Controller.ps1 -Mode Prepare\nЗапускайте PowerShell от администратора. После завершения загрузите result.json из Runs. Экспорт не подтверждает готовность.";
        }
        catch (Exception ex)
        {
            HostStatusBox.Text = ex.Message;
        }
    }

    /// <summary>Загружает снимок только при совпадении выбранного компьютера и параметров.</summary>
    private void OnImportHostClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveForOperation();
            var dialog = new OpenFileDialog { Title = "Отчёт контроллера PostgreSQL", Filter = "JSON report|*.json", CheckFileExists = true };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                HostStatusBox.Text = DescribeHost(new PostgreSqlHostSetupService().ReadReport(SelectedProfile(), dialog.FileName));
        }
        catch (Exception ex)
        {
            HostStatusBox.Text = ex.Message;
        }
    }

    /// <summary>Запуск установщика отличается от подтверждённой готовности контроллера.</summary>
    private static string DescribeHost(PostgreSqlHostSetupReport report) => $"{(report.Ready ? "Готовность локального контроллера подтверждена" : "Готовность локального контроллера не подтверждена")}\nМомент проверки: {report.CheckedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nКомпьютер: {report.Computer}\nХост: {report.HostKind}; служба: {report.HostService}; состояние: {report.HostServiceState}; PID: {report.HostProcessId}\nКонтроллер: {report.Local?.ControllerState}; владение: {report.Local?.OwnsController}; права: {report.Local?.PermissionsOk}\nКоманды включены: {report.Local?.Settings.CommandsEnabled}\nPostgreSQL: {report.Local?.PostgreSqlRole}; кластер: {report.Local?.SystemIdentifier}\nУстановщик выполнен: {report.PreparationCompleted}\nНастройки: {report.SettingsPath}\n{report.Message}\nЖурнал на сервере: {report.LogPath}\nЭто снимок на указанное время; для текущего состояния выполните новую проверку.";
}
