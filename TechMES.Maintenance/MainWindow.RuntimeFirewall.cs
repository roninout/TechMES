using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

/// <summary>
/// Настройка доступа компьютеров WEB к локальному Runtime API.
/// </summary>
public partial class MainWindow
{
    private bool _runtimeFirewallBusy;
    private string _runtimeFirewallStatus = "Not checked";

    /// <summary>
    /// Разрешённые IP-адреса сохраняются в maintenance.settings.json.
    /// </summary>
    public string ServerRuntimeFirewallRemoteAddresses
    {
        get => _configuration.Server.RuntimeFirewallRemoteAddresses;
        set
        {
            _configuration.Server.RuntimeFirewallRemoteAddresses = value;

            OnPropertyChanged(nameof(ServerRuntimeFirewallRemoteAddresses));

            ServerRuntimeFirewallStatus = "Settings changed; apply the Runtime firewall rule.";
        }
    }

    /// <summary>
    /// Результат настройки правила. Доступность API проверяется отдельно.
    /// </summary>
    public string ServerRuntimeFirewallStatus
    {
        get => _runtimeFirewallStatus;
        private set
        {
            _runtimeFirewallStatus = value;
            OnPropertyChanged(nameof(ServerRuntimeFirewallStatus));
        }
    }

    /// <summary>
    /// Проверяет ввод, сохраняет профиль Maintenance и применяет правило.
    /// Повторное нажатие обновляет существующее правило.
    /// </summary>
    private async void OnOpenRuntimeFirewallClick(object sender, RoutedEventArgs e)
    {
        if (_runtimeFirewallBusy)
            return;

        var button = sender as Button;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Start Maintenance as administrator to configure the firewall.");

            var port = RuntimeFirewallRule.GetPort(TypedAppSettings.RuntimeUrls);
            var script = RuntimeFirewallRule.BuildScript(port, ServerRuntimeFirewallRemoteAddresses);

            _configurationStore.Save(_configuration);

            _runtimeFirewallBusy = true;

            if (button is not null)
                button.IsEnabled = false;

            ServerRuntimeFirewallStatus = "Applying...";

            AppendServerLog($"Configuring Runtime firewall: TCP {port}; WEB addresses: {ServerRuntimeFirewallRemoteAddresses}.");

            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var result = await new ProcessRunner().RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", command], TimeSpan.FromSeconds(30));

            AppendServerLog(result.CombinedOutput);

            if (result.ExitCode != 0)
                throw new InvalidOperationException("Runtime firewall configuration failed. See Server log for details.");

            ServerRuntimeFirewallStatus = $"Configured: TCP {port}. Verify API access from the WEB computers.";
        }
        catch (Exception ex)
        {
            ServerRuntimeFirewallStatus = ex.Message;

            AppendServerLog($"Runtime firewall: {ex.Message}");

            MessageBox.Show(this, ex.Message, "Runtime firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runtimeFirewallBusy = false;

            if (button is not null)
                button.IsEnabled = true;
        }
    }
}