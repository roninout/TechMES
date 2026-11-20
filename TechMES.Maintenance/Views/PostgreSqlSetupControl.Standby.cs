using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSetupControl
{
    private string? _initialStandbyDirectory;

    private void OnInitialStandbyExpanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WizardInitialStandby) || Cards.Count != 2)
            return;

        if (InitialStandbySource.Text.Length == 0)
            InitialStandbySource.Text = GetProfileAddress(Cards[0]);
        if (InitialStandbyTarget.Text.Length == 0)
            InitialStandbyTarget.Text = GetProfileAddress(Cards[1]);
        if (InitialStandbySystemIdentifier.Text.Length == 0
            && (Cards[0].Live ?? Cards[0].Report?.Local) is { PostgreSqlRole: "Primary", SystemIdentifier: { } identifier })
            InitialStandbySystemIdentifier.Text = identifier;
    }

    private async void OnExportInitialStandbyClick(object sender, RoutedEventArgs e)
    {
        var directoryDialog = new OpenFolderDialog { Title = "Папка для комплекта создания Standby" };
        if (Window.GetWindow(this) is not MainWindow window || directoryDialog.ShowDialog(window) != true)
            return;

        SetCardActionsEnabled(false);
        try
        {
            if (_loadFailed || Cards.Count != 2)
                throw new InvalidDataException("Проверьте и сохраните оба профиля PostgreSQL.");

            var sourceAddress = InitialStandbySource.Text.Trim();
            var targetAddress = InitialStandbyTarget.Text.Trim();
            var identifier = InitialStandbySystemIdentifier.Text.Trim();
            if (Cards[1].LiveNode is { Local.PostgreSqlRole: "Standby" } standby
                && standby.NodeAddress == targetAddress && standby.Local.SystemIdentifier == identifier
                && PostgreSqlWizardDiagnostics.IsFresh(standby.Local.CheckedAtUtc, standby.Local.HealthPeriodSeconds, DateTimeOffset.UtcNow))
                throw new InvalidDataException($"Узел {targetAddress} уже работает как Standby этого кластера. Для проверки откройте пункт 2.");

            var service = new PostgreSqlInitialStandbyService(window.PostgreSqlHostToolsDirectory);
            var directory = await Task.Run(() => service.Export(directoryDialog.FolderName,
                Cards[0].Profile, sourceAddress, Cards[1].Profile, targetAddress, identifier));
            _initialStandbyDirectory = directory;
            InitialStandbyOpenFolder.IsEnabled = true;

            var serviceName = Cards[1].Profile.ServiceName;
            const string check = @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup-Standby.ps1 -Mode Check";
            InitialStandbyResult.Text = $"Скопируйте папку {directory} целиком на новый сервер {targetAddress}.\n"
                + $"На {targetAddress} остановите службу {serviceName} и установите тип запуска «Отключена». "
                + "Откройте скопированную папку в Windows PowerShell от администратора.\n"
                + check + "\nПосле успешной проверки PowerShell напечатает точную команду Apply. "
                + "После подтверждения streaming продолжите с проверки контроллеров в пункте 1.";
        }
        catch (Exception ex)
        {
            InitialStandbyResult.Text = ex.Message;
        }
        finally
        {
            SetCardActionsEnabled(true);
        }
    }

    private void OnOpenInitialStandbyFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_initialStandbyDirectory is { } directory)
                using (Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })) { }
        }
        catch (Exception ex)
        {
            InitialStandbyResult.Text = ex.Message;
        }
    }
}
