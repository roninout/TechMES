using System.Security.Principal;
using System.Text;
using System.Windows;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    /// <summary>Настраивает следующую загрузку установленных служб TechMES без их перезапуска.</summary>
    private async void OnConfigureServiceStartupClick(object sender, RoutedEventArgs e)
    {
        if (_deploymentOperationRunning) return;

        using var identity = WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show(this, "Запустите Maintenance от имени администратора.", "Автозапуск TechMES", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _deploymentOperationRunning = true;
        IsEnabled = false;

        var report = new StringBuilder();
        var success = true;

        try
        {
            var runner = new ProcessRunner();

            // Точный список исключает изменение PostgreSQL, Agent и сторонних служб.
            foreach (var name in new[] { "TechMES.Runtime.Service", "TechMES.Calc.Service", "TechMES.Web", "TechMES.Host" })
            {
                var query = await _serviceManager.QueryAsync(name);

                if (!query.Success)
                {
                    var missing = query.Details.Contains("1060", StringComparison.Ordinal);
                    success &= missing;

                    report.AppendLine(missing
                        ? $"{name}: не установлена, пропущена."
                        : $"{name}: ошибка чтения. {query.Details}");

                    continue;
                }

                var result = await runner.RunAsync("sc.exe", ["config", name, "start=", "delayed-auto"], TimeSpan.FromSeconds(10));

                success &= result.ExitCode == 0;

                report.AppendLine(result.ExitCode == 0
                    ? $"{name}: отложенный автоматический запуск настроен."
                    : $"{name}: ошибка настройки. {result.CombinedOutput}");
            }

            report.AppendLine("Настройка применяется при следующей загрузке Windows. Службы не перезапускались.");

            AppendDiagnostics(report.ToString());
            MessageBox.Show(this, report.ToString(), "Автозапуск TechMES", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            report.AppendLine("Ошибка: " + ex.Message);

            AppendDiagnostics(report.ToString());
            MessageBox.Show(this, report.ToString(), "Автозапуск TechMES", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _deploymentOperationRunning = false;
            IsEnabled = true;
        }
    }
}