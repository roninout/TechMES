using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

/// <summary>Два профиля, общий архив и доставка без удалённого запуска или смены учётной записи.</summary>
public partial class MainWindow
{
    private PreparedDeployment? _preparedDeployment;
    private string _packageDeploymentLog = "";
    private static string PackageDeploymentProfilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "Maintenance", "package-deployment.json");
    public PackageDeploymentOptions PackageDeployment { get; } = LoadPackageDeployment();
    public string PreparedDeploymentFolder => _preparedDeployment?.Directory ?? "Комплект пока не подготовлен.";
    public string PackageDeploymentLog { get => _packageDeploymentLog; private set { _packageDeploymentLog = value; OnPropertyChanged(nameof(PackageDeploymentLog)); } }

    /// <summary>Читает отдельные профили; повреждение файла требует явного исправления и сохранения.</summary>
    private static PackageDeploymentOptions LoadPackageDeployment()
    {
        try
        {
            var options = File.Exists(PackageDeploymentProfilePath) ? JsonSerializer.Deserialize<PackageDeploymentOptions>(File.ReadAllText(PackageDeploymentProfilePath), DeploymentPackageService.JsonOptions) ?? throw new InvalidDataException("Пустой профиль.") : new();
            options.Validate(false);
            return options;
        }
        catch (Exception ex) { return new() { LoadError = "Не удалось прочитать профиль: " + ex.Message }; }
    }

    /// <summary>Добавляет результат операции в журнал нового меню Deployment.</summary>
    private void AppendPackageDeploymentLog(string message) => PackageDeploymentLog += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

    /// <summary>Выбирает один готовый ZIP для обоих профилей.</summary>
    private void OnChooseDeploymentArchiveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Общий архив TechMES", Filter = "TechMES ZIP|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        PackageDeployment.ArchivePath = dialog.FileName;
        OnPropertyChanged(nameof(PackageDeployment));
    }

    /// <summary>Выбирает локальную папку подготовки, доступную и для ручного копирования.</summary>
    private void OnChooseDeploymentOutputClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Локальная папка комплектов развёртывания" };
        if (dialog.ShowDialog(this) != true) return;
        PackageDeployment.OutputFolder = dialog.FolderName;
        OnPropertyChanged(nameof(PackageDeployment));
    }

    /// <summary>Атомарно сохраняет два профиля независимо от обновляемых appsettings служб.</summary>
    private void SavePackageDeploymentProfiles()
    {
        PackageDeployment.Validate(false);
        Directory.CreateDirectory(Path.GetDirectoryName(PackageDeploymentProfilePath)!);
        var temporary = PackageDeploymentProfilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(PackageDeployment, DeploymentPackageService.JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, PackageDeploymentProfilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        PackageDeployment.LoadError = "";
        OnPropertyChanged(nameof(PackageDeployment));
    }

    /// <summary>Сохраняет оба профиля по кнопке, даже если архив ещё не создан.</summary>
    private void OnSavePackageDeploymentClick(object sender, RoutedEventArgs e)
    {
        try { SavePackageDeploymentProfiles(); AppendPackageDeploymentLog("Профили сохранены: " + PackageDeploymentProfilePath); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Профили Deployment", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Готовит один ZIP с двумя скриптами и их отдельные копии для запуска рядом с ZIP.</summary>
    private async void OnPrepareDeploymentPackageClick(object sender, RoutedEventArgs e)
    {
        if (_deploymentOperationRunning) return;
        _deploymentOperationRunning = true;
        IsEnabled = false;
        try
        {
            if (PackageDeployment.LoadError.Length != 0) throw new InvalidDataException(PackageDeployment.LoadError + " Исправьте поля и сохраните профили.");
            SavePackageDeploymentProfiles();
            var updater = GetDeploymentTool("Update-TechMESServer.ps1");
            AppendPackageDeploymentLog("Подготовка общего ZIP и двух локальных скриптов...");
            _preparedDeployment = await Task.Run(() => new DeploymentPackageService().Prepare(PackageDeployment, updater));
            OnPropertyChanged(nameof(PreparedDeploymentFolder));
            AppendPackageDeploymentLog("Готово: " + _preparedDeployment.Directory);
            AppendPackageDeploymentLog("Для каждого сервера скопируйте TechMES_Deployment.zip и его Deploy-Primary.ps1 либо Deploy-Secondary.ps1. Запуск выполняется на самом сервере.");
        }
        catch (Exception ex) { AppendPackageDeploymentLog("Подготовка не завершена: " + ex.Message); MessageBox.Show(this, ex.Message, "Подготовка комплекта", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _deploymentOperationRunning = false; IsEnabled = true; }
    }

    /// <summary>Пробует доставить файлы выбранному серверу; отказ доступа оставляет ручной способ доступным.</summary>
    private async void OnSendDeploymentPackageClick(object sender, RoutedEventArgs e)
    {
        if (_deploymentOperationRunning || sender is not FrameworkElement { Tag: ServerDeploymentProfile profile }) return;
        if (_preparedDeployment is null) { MessageBox.Show(this, "Сначала нажмите «Подготовить комплект».", "Send", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _deploymentOperationRunning = true;
        IsEnabled = false;
        try
        {
            AppendPackageDeploymentLog(profile.Name + ": Send — копируем ZIP и скрипт...");
            var destination = await Task.Run(() => new DeploymentPackageService().Send(PackageDeployment, _preparedDeployment, profile));
            AppendPackageDeploymentLog(profile.Name + ": файлы доставлены в " + destination + ". Запустите Deploy-" + profile.Name + ".ps1 на этом сервере от администратора.");
        }
        catch (Exception ex)
        {
            AppendPackageDeploymentLog(profile.Name + ": Send не выполнен: " + ex.Message);
            AppendPackageDeploymentLog("Комплект сохранён локально: " + _preparedDeployment.Directory + ". Скопируйте ZIP и Deploy-" + profile.Name + ".ps1 вручную.");
        }
        finally { _deploymentOperationRunning = false; IsEnabled = true; }
    }

    /// <summary>Открывает папку подготовленных файлов для просмотра или ручного копирования.</summary>
    private void OnOpenDeploymentOutputClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _preparedDeployment?.Directory ?? PackageDeployment.OutputFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Папка комплекта", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
