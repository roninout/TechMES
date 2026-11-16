using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Views;

/// <summary>
/// Просмотр и явное подтверждение доверия
/// к публичным WEB-сертификатам.
/// </summary>
public partial class WebCertificateTrustControl : UserControl
{
    private WebCertificateTrust[] _selected = [];

    public WebCertificateTrustControl() => InitializeComponent();

    private void OnChooseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите публичные WEB CER обоих узлов",
            Filter = "Публичный сертификат (*.cer)|*.cer",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        _selected = [];

        try
        {
            _selected = dialog.FileNames.Select(WebCertificateTrust.Load).ToArray();
            RefreshDetails();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshDetails();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshDetails()
    {
        RefreshButton.IsEnabled = _selected.Length > 0;
        InstallButton.IsEnabled = false;

        DetailsBox.Text = string.Join("\n\n────────────────────\n\n", _selected.Select(certificate => certificate.Describe()));

        try
        {
            foreach (var certificate in _selected)
                certificate.ValidateForImport();

            InstallButton.IsEnabled = _selected.Length > 0;
        }
        catch (InvalidOperationException)
        {
            // Причина уже показана в описании CER.
        }
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Запустите Maintenance от имени администратора и снова выберите CER.");

            RefreshDetails();

            if (!InstallButton.IsEnabled)
                return;

            var confirmation =
                "Сверьте SHA256 с исходными узлами и проверьте нужное DNS-имя в SAN.\n\n" +
                "Установить доверие к следующим WEB-сертификатам для всех пользователей этого компьютера?\n\n" +
                DetailsBox.Text;

            if (MessageBox.Show(Window.GetWindow(this), confirmation, "Доверие к WEB CER", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            foreach (var certificate in _selected)
                certificate.Install();

            RefreshDetails();

            DetailsBox.Text =
                "Доверие установлено. Проверьте HTTPS через общий адрес при каждом владельце IP.\n\n" +
                DetailsBox.Text;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        InstallButton.IsEnabled = false;
        RefreshButton.IsEnabled = _selected.Length > 0;

        // При ошибке после первого импорта не заявляем откат:
        // показываем фактическое состояние каждого сертификата.
        var reports = _selected.Select(certificate =>
        {
            try
            {
                return certificate.Describe();
            }
            catch (Exception error)
            {
                return $"{certificate.SourcePath}: {error.Message}";
            }
        });

        DetailsBox.Text = $"Ошибка: {ex.Message}\n\n" + string.Join("\n\n", reports);
    }
}