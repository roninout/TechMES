using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSetupControl
{
    private string? _networkBundleDirectory;

    private void OnNetworkWizardExpanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WizardNetwork) || Cards.Count != 2)
            return;

        if (NetworkFirstAddress.Text.Length == 0)
            NetworkFirstAddress.Text = GetProfileAddress(Cards[0]);
        if (NetworkSecondAddress.Text.Length == 0)
            NetworkSecondAddress.Text = GetProfileAddress(Cards[1]);

        var identifiers = Cards.Select(card => (card.Live ?? card.Report?.Local)?.SystemIdentifier).Distinct().ToArray();
        if (NetworkSystemIdentifier.Text.Length == 0 && identifiers is [string identifier])
            NetworkSystemIdentifier.Text = identifier;
    }

    private static string GetProfileAddress(ServerCard card)
    {
        if (card.LiveNode is { } live)
            return live.NodeAddress;
        if (IPAddress.TryParse(card.Profile.ComputerName, out var configured) && configured.AddressFamily == AddressFamily.InterNetwork)
            return card.Profile.ComputerName;
        return card.Report?.Addresses is [string address] ? address : "";
    }

    private void OnSelectNetworkCertificateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Открытый сертификат", Filter = "Сертификат (*.cer)|*.cer", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var field = (sender as FrameworkElement)?.Tag switch
        {
            "First" => NetworkFirstCertificate,
            "Second" => NetworkSecondCertificate,
            _ => NetworkMaintenanceCertificate
        };
        field.Text = dialog.FileName;
    }

    private async void OnPrepareNetworkNodesClick(object sender, RoutedEventArgs e) => await RunNetworkStepAsync(PostgreSqlNetworkSetupStage.PrepareNodes);
    private async void OnConnectNetworkNodesClick(object sender, RoutedEventArgs e) => await RunNetworkStepAsync(PostgreSqlNetworkSetupStage.ConnectNodes);
    private async void OnPrepareNetworkMaintenanceClick(object sender, RoutedEventArgs e) => await RunNetworkStepAsync(PostgreSqlNetworkSetupStage.PrepareMaintenance);
    private async void OnTrustNetworkMaintenanceClick(object sender, RoutedEventArgs e) => await RunNetworkStepAsync(PostgreSqlNetworkSetupStage.TrustMaintenance);

    private async Task RunNetworkStepAsync(PostgreSqlNetworkSetupStage stage)
    {
        const string launch = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup-Network.ps1";
        try
        {
            if (_loadFailed || Cards.Count != 2)
                throw new InvalidDataException("Проверьте и сохраните оба профиля PostgreSQL.");
            if (Window.GetWindow(this) is not MainWindow window)
                throw new InvalidOperationException("Откройте мастер из главного окна Maintenance.");
            if (!int.TryParse(NetworkPort.Text, out var port))
                throw new InvalidDataException("Введите номер порта mTLS.");

            var first = new PostgreSqlNetworkSetupNode(Cards[0].Profile, NetworkFirstAddress.Text.Trim());
            var second = new PostgreSqlNetworkSetupNode(Cards[1].Profile, NetworkSecondAddress.Text.Trim());
            PostgreSqlPublicCertificate? firstCert = null;
            PostgreSqlPublicCertificate? secondCert = null;
            PostgreSqlPublicCertificate? maintenanceCert = null;
            if (stage is PostgreSqlNetworkSetupStage.ConnectNodes or PostgreSqlNetworkSetupStage.PrepareMaintenance)
            {
                firstCert = PostgreSqlNetworkSetupService.ReadCertificate(NetworkFirstCertificate.Text, NetworkFirstPin.Text.Trim(), first.Address);
                secondCert = PostgreSqlNetworkSetupService.ReadCertificate(NetworkSecondCertificate.Text, NetworkSecondPin.Text.Trim(), second.Address);
            }
            if (stage == PostgreSqlNetworkSetupStage.TrustMaintenance)
                maintenanceCert = PostgreSqlNetworkSetupService.ReadCertificate(NetworkMaintenanceCertificate.Text, NetworkMaintenancePin.Text.Trim(), null);

            var systemIdentifier = NetworkSystemIdentifier.Text.Trim();
            var maintenanceAddress = NetworkMaintenanceAddress.Text.Trim();
            var dialog = new OpenFolderDialog { Title = "Папка для комплекта настройки связи PostgreSQL" };
            if (dialog.ShowDialog(window) != true)
                return;

            SetCardActionsEnabled(false);
            _networkBundleDirectory = null;
            NetworkOpenFolder.IsEnabled = false;
            NetworkResult.Text = "Подготавливаем комплект…";
            var service = new PostgreSqlNetworkSetupService(window.PostgreSqlHostToolsDirectory);
            var folder = await Task.Run(() => service.Export(dialog.FolderName, stage, first, second, systemIdentifier, port,
                maintenanceAddress, firstCert, secondCert, maintenanceCert));

            _networkBundleDirectory = folder;
            NetworkOpenFolder.IsEnabled = true;
            if (stage == PostgreSqlNetworkSetupStage.PrepareMaintenance)
            {
                NetworkResult.Text = "Подтвердите запуск PowerShell с правами администратора на компьютере Maintenance.";
                await window.RunLocalPostgreSqlNetworkSetupAsync(folder);
                using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "result.json")));
                var pin = result.RootElement.GetProperty("MaintenanceSha256").GetString() ?? "";
                var certificate = Path.Combine(folder, "public.cer");
                PostgreSqlNetworkSetupService.ReadCertificate(certificate, pin, null);
                NetworkMaintenanceCertificate.Text = certificate;
                NetworkMaintenancePin.Text = pin;
                NetworkResult.Text = "Клиентский сертификат готов. Экспортируйте доверие к Maintenance для обоих узлов (D).";
                return;
            }

            var next = stage switch
            {
                PostgreSqlNetworkSetupStage.PrepareNodes => "Перенесите оба public.cer в Maintenance. SHA-256 возьмите из вывода на соответствующем сервере. Затем выполните B.",
                PostgreSqlNetworkSetupStage.ConnectNodes => "Дождитесь успешного запуска комплектов на обоих узлах. Комплект перезапускает контроллер своего узла. Затем выполните C.",
                _ => "Комплект перезапускает контроллер своего узла. После успешного запуска обоих комплектов нажмите «Проверить пару» в пункте 2."
            };
            NetworkResult.Text = $"{folder}\nСкопируйте на каждый сервер папку с его IP. В этой папке запустите команду в Windows PowerShell от администратора.\n{launch}\n{next}";
        }
        catch (Exception ex)
        {
            NetworkResult.Text = ex.Message;
        }
        finally
        {
            SetCardActionsEnabled(true);
        }
    }

    private void OnOpenNetworkFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_networkBundleDirectory is { } folder)
                using (Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })) { }
        }
        catch (Exception ex)
        {
            NetworkResult.Text = ex.Message;
        }
    }
}
