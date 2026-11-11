using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

/// <summary>Сборка на компьютере разработки и локальное транзакционное обновление служб.</summary>
public partial class MainWindow
{
    private bool _deploymentOperationRunning;
    public bool CanPublishFromSource => File.Exists(Path.Combine(_repositoryRoot.FullName, "TechMES.sln")) && _configuration.Services.Count > 0 && _configuration.Services.All(service => !string.IsNullOrWhiteSpace(service.ProjectPath) && File.Exists(Path.Combine(_repositoryRoot.FullName, service.ProjectPath)));
    public bool CanInstallFromSource => CanPublishFromSource;
    public string MaintenanceSettingsPath => _configurationStore.ConfigurationPath;
    public string DeploymentModeText => CanPublishFromSource ? "Deploy all собирает проекты, обновляет локальные службы и запускает их. Создать архив — собирает переносимый пакет для Deployment." : "Для Deploy all и создания архива выберите исходники в Settings → Server. Готовые архивы настраиваются в меню Deployment.";

    /// <summary>Запоминает выбранный репозиторий; все файловые службы получают его после перезапуска.</summary>
    private void OnChooseRepositoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите TechMES.sln", Filter = "TechMES solution|TechMES.sln", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var root = new DirectoryInfo(Path.GetDirectoryName(dialog.FileName)!);
            new RepositoryLocator().RememberRepositoryRoot(root);
            MessageBox.Show(this, $"Сохранён путь: {root.FullName}\nПерезапустите Maintenance для применения.", "TechMES", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Путь к репозиторию", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>
    /// Проверяет исходники и доступный SDK общим инструментом сборки.
    /// Проверка выполняется до остановки служб и изменения публикации.
    /// </summary>
    private async Task<bool> CheckSourceDeploymentAsync()
    {
        var error = CanPublishFromSource
            ? null
            : "Не найдены TechMES.sln или проекты профиля. Выберите репозиторий в Settings → Server.";

        if (error is null)
        {
            try
            {
                var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

                var result = await new ProcessRunner().RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", GetDeploymentTool("Build-TechMESUpdate.ps1"), "-SdkOnly"], TimeSpan.FromSeconds(30));

                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                    AppendDeploymentLog(result.StandardOutput.Trim());

                if (!string.IsNullOrWhiteSpace(result.StandardError))
                    AppendDeploymentLog(result.StandardError.Trim());

                if (result.ExitCode != 0)
                    error = "Не найден подходящий .NET SDK 10 или новее. Подробности — в Deploy log. SDK нужен только на компьютере сборки.";
            }
            catch (Exception ex)
            {
                error = "Не удалось проверить инструменты сборки: " + ex.Message;
            }
        }

        if (error is null)
            return true;

        AppendDeploymentLog(error);

        MessageBox.Show(this, error, "Сборка не запущена", MessageBoxButton.OK, MessageBoxImage.Information);

        return false;
    }

    /// <summary>Находит локальный инструмент в публикации Maintenance либо в исходниках.</summary>
    private string GetDeploymentTool(string name)
    {
        var published = Path.Combine(AppContext.BaseDirectory, "Tools", name);
        if (File.Exists(published)) return published;
        var source = Path.Combine(_repositoryRoot.FullName, "scripts", "deploy", name);
        return File.Exists(source) ? source : throw new FileNotFoundException("Отсутствует инструмент: " + name);
    }

    /// <summary>Читает поток процесса построчно, чтобы журнал оставался видимым во время сборки.</summary>
    private static async Task PumpDeploymentOutputAsync(StreamReader reader, Action<string> log)
    {
        while (await reader.ReadLineAsync() is { } line) log(line);
    }

    /// <summary>Запускает PowerShell без принудительного таймаута, который мог бы оборвать откат.</summary>
    private async Task<int> RunDeploymentToolAsync(string name, IReadOnlyList<string> arguments, Action<string> log)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-File", GetDeploymentTool(name) }.Concat(arguments)) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить " + name);
        var output = PumpDeploymentOutputAsync(process.StandardOutput, log);
        var error = PumpDeploymentOutputAsync(process.StandardError, log);
        await Task.WhenAll(process.WaitForExitAsync(), output, error);
        return process.ExitCode;
    }

    /// <summary>Собирает в отдельную папку: ошибка компиляции не затрагивает работающие службы.</summary>
    private async Task<(string PackageRoot, string ArchivePath)> BuildDeploymentPackageAsync(string outputFolder, bool archive)
    {
        string root = "", zip = "";
        var arguments = new List<string> { "-RepositoryRoot", _repositoryRoot.FullName, "-OutputRoot", outputFolder, "-Configuration", _configuration.Deployment.Configuration };
        if (!archive) arguments.AddRange(["-NoArchive", "-ServicesOnly"]);
        var exitCode = await RunDeploymentToolAsync("Build-TechMESUpdate.ps1", arguments, line =>
        {
            AppendDeploymentLog(line);
            if (line.StartsWith("PACKAGE_ROOT=", StringComparison.Ordinal)) root = line[13..];
            if (line.StartsWith("ARCHIVE_PATH=", StringComparison.Ordinal)) zip = line[13..];
        });
        if (exitCode != 0 || root.Length == 0 || (archive && zip.Length == 0)) throw new InvalidOperationException("Сборка не завершена. Проверьте Deploy log; работающие службы не обновлялись.");
        return (root, zip);
    }

    /// <summary>Создаёт переносимый архив рядом с выбранной папкой, не обновляя локальные службы.</summary>
    private async void OnCreateDeploymentArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_deploymentOperationRunning || !await CheckSourceDeploymentAsync()) return;
        var dialog = new OpenFolderDialog { Title = "Папка готовых архивов TechMES" };
        if (dialog.ShowDialog(this) != true) return;
        _deploymentOperationRunning = true;
        IsEnabled = false;
        try
        {
            var result = await BuildDeploymentPackageAsync(dialog.FolderName, true);
            PackageDeployment.ArchivePath = result.ArchivePath;
            OnPropertyChanged(nameof(PackageDeployment));
            AppendDeploymentLog("Архив создан: " + result.ArchivePath);
            MessageBox.Show(this, "Архив создан. В меню Deployment заполните оба профиля и нажмите «Подготовить комплект».", "TechMES", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { AppendDeploymentLog(ex.Message); MessageBox.Show(this, ex.Message, "Создание архива", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _deploymentOperationRunning = false; IsEnabled = true; }
    }

    /// <summary>Выполняет Deploy all из исходников через общий локальный установщик с откатом.</summary>
    private async Task DeployAllFromSourceAsync()
    {
        if (_deploymentOperationRunning || !await CheckSourceDeploymentAsync()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show(this, "Запустите Maintenance от имени администратора.", "Deploy all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _deploymentOperationRunning = true;
        IsEnabled = false;
        try
        {
            AppendDeploymentLog("Deploy all started. Сначала собираем проекты в отдельную папку.");
            var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "Builds");
            var result = await BuildDeploymentPackageAsync(output, false);
            foreach (var service in _configuration.Services)
            {
                var target = _deploymentManager.GetPublishDirectory(service, _configuration.Deployment);
                if (File.Exists(Path.Combine(target, "appsettings.json"))) continue;
                var state = await _serviceManager.QueryAsync(service.ServiceName);
                if (state.Success || !state.Details.Contains("1060", StringComparison.Ordinal)) throw new InvalidOperationException("Не найден appsettings.json установленной службы: " + target);
                var source = Path.GetDirectoryName(Path.Combine(_repositoryRoot.FullName, service.ProjectPath ?? throw new InvalidDataException("ProjectPath is empty.")))!;
                Directory.CreateDirectory(target);
                foreach (var settings in Directory.GetFiles(source, "appsettings*.json"))
                    if (!File.Exists(Path.Combine(target, Path.GetFileName(settings)))) File.Copy(settings, Path.Combine(target, Path.GetFileName(settings)));
            }
            var json = JsonSerializer.Serialize(new { Name = "Development", ComputerName = Environment.MachineName, InstallRoot = _configuration.Deployment.PublishRoot, RuntimeStatusUrl = GetRuntimeDiagnosticsBaseUrl().TrimEnd('/') + "/api/runtime/status", Components = new[] { "runtime", "calc", "web" }, ManifestSha256 = DeploymentPackageService.HashFile(Path.Combine(result.PackageRoot, "manifest.json")), ArchiveName = "" });
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var exitCode = await RunDeploymentToolAsync("Update-TechMESServer.ps1", ["-PackageRoot", result.PackageRoot, "-ProfileBase64", encoded], AppendDeploymentLog);
            if (exitCode != 0) throw new InvalidOperationException("Deploy all не завершён. Проверьте журнал установщика и результат отката.");
            AppendDeploymentLog("Deploy all finished successfully.");
            try { await Task.Run(() => Directory.Delete(result.PackageRoot, true)); }
            catch (Exception cleanup) { AppendDeploymentLog("Службы обновлены. Не удалось удалить временную папку сборки: " + cleanup.Message); }
        }
        catch (Exception ex) { AppendDeploymentLog("Deploy all FAILED: " + ex.Message); MessageBox.Show(this, ex.Message, "Deploy all", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _deploymentOperationRunning = false; IsEnabled = true; }
    }

    /// <summary>Не позволяет закрыть Maintenance во время сборки, обновления или отката.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_deploymentOperationRunning)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Дождитесь завершения текущей операции.", "TechMES", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        base.OnClosing(e);
    }
}
