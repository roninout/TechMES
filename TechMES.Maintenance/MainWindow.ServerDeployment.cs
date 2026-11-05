using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

/// <summary>
/// Разделяет сборку из исходников и обновление сервера из готового пакета.
/// </summary>
public partial class MainWindow
{
    private bool _packageUpdateRunning;

    /// <summary>
    /// Разрешает сборку только при наличии solution и всех проектов из профиля.
    /// </summary>
    public bool CanPublishFromSource =>
        File.Exists(Path.Combine(_repositoryRoot.FullName, "TechMES.sln")) &&
        _configuration.Services.Count > 0 &&
        _configuration.Services.All(service =>
            !string.IsNullOrWhiteSpace(service.ProjectPath) &&
            File.Exists(Path.Combine(_repositoryRoot.FullName, service.ProjectPath)));

    /// <summary>
    /// Показывает фактический файл профиля, включая серверный каталог ProgramData.
    /// </summary>
    public string MaintenanceSettingsPath => _configurationStore.ConfigurationPath;

    /// <summary>
    /// Объясняет назначение кнопок Deploy для текущего компьютера.
    /// </summary>
    public string DeploymentModeText => CanPublishFromSource
        ? "Исходники найдены. Для обновления работающих служб используйте Update from package."
        : "Сервер без исходников: используйте Update from package. Repository path и .NET SDK для этого не нужны.";

    /// <summary>
    /// Запоминает выбранный репозиторий для следующего запуска Maintenance.
    /// Перезапуск нужен, чтобы все файловые службы получили один и тот же корень.
    /// </summary>
    private void OnChooseRepositoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите TechMES.sln на компьютере с исходниками",
            Filter = "TechMES solution (TechMES.sln)|TechMES.sln",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var root = new DirectoryInfo(Path.GetDirectoryName(dialog.FileName)!);
            new RepositoryLocator().RememberRepositoryRoot(root);

            MessageBox.Show(
                this,
                $"Сохранён путь: {root.FullName}\nПерезапустите Maintenance для применения.",
                "TechMES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось сохранить путь", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Проверяет исходники и SDK до любых действий развёртывания.
    /// На сервере предлагает готовый пакет вместо попытки dotnet publish.
    /// </summary>
    private async Task<bool> CheckSourceDeploymentAsync()
    {
        string? error = null;

        if (!CanPublishFromSource)
        {
            error = "Не найдены TechMES.sln или проекты профиля. На сервере выберите Update from package; исходники копировать не нужно.";
        }
        else
        {
            var result = await new ProcessRunner().RunAsync(
                "dotnet",
                ["--list-sdks"],
                TimeSpan.FromSeconds(15));

            if (result.ExitCode != 0 ||
                !result.StandardOutput.Split('\n').Any(line =>
                    line.TrimStart().StartsWith("10.", StringComparison.Ordinal)))
            {
                error = "Для сборки из исходников нужен .NET SDK 10. Готовый пакет обновления SDK не требует.";
            }
        }

        if (error is null)
            return true;

        AppendDeploymentLog(error);
        MessageBox.Show(this, error, "Публикация не запущена", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>
    /// Выбирает manifest распакованного пакета и запускает штатный updater.
    /// Скрипт берётся из папки Maintenance, а не из выбранного пакета.
    /// </summary>
    private async void OnUpdateFromPackageClick(object sender, RoutedEventArgs e)
    {
        if (_packageUpdateRunning)
            return;

        using var identity = WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show(
                this,
                "Запустите Maintenance от имени администратора.",
                "Обновление TechMES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Выберите manifest.json в распакованном TechMES_Update_*",
            Filter = "TechMES package (manifest.json)|manifest.json",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "Tools", "Update-TechMESServer.ps1");

            if (!File.Exists(helper))
            {
                throw new FileNotFoundException(
                    "Отсутствует Tools/Update-TechMESServer.ps1. Перенесите всю папку Maintenance из нового пакета.",
                    helper);
            }

            var packageRoot = Path.GetDirectoryName(dialog.FileName)!;
            var runtimeUrl = GetRuntimeDiagnosticsBaseUrl().TrimEnd('/') + "/api/runtime/status";

            var logFolder = Path.Combine(Path.GetDirectoryName(MaintenanceSettingsPath)!, "UpdateLogs");
            Directory.CreateDirectory(logFolder);

            var logPath = Path.Combine(
                logFolder,
                $"update-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");

            // Литералы экранируются для PowerShell.
            // Out-File сохраняет UTF-8 без зависимости от кодовой страницы консоли.
            var command = "$ErrorActionPreference = 'Stop'\ntry {\n& " + PowerShellLiteral(helper) +
                " -PackageRoot " + PowerShellLiteral(packageRoot) +
                " -RuntimeStatusUrl " + PowerShellLiteral(runtimeUrl) +
                " *>&1 | Out-File -LiteralPath " + PowerShellLiteral(logPath) + " -Encoding UTF8 -Width 240\n" +
                "if (!$?) { exit 1 }; exit 0\n} catch { $_ | Out-String | Add-Content -LiteralPath " +
                PowerShellLiteral(logPath) + " -Encoding UTF8; exit 1 }";

            var startInfo = new ProcessStartInfo(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

            _packageUpdateRunning = true;
            IsEnabled = false;

            AppendDeploymentLog(
                $"Package update started: {packageRoot}. Ожидайте завершения; проверка и откат выполняются скриптом.");

            AppendDeploymentLog($"Update log: {logPath}");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить updater.");

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            // ProcessRunner здесь не используется:
            // его таймаут мог бы оборвать восстановление файлов.
            await process.WaitForExitAsync();

            if (File.Exists(logPath))
                AppendDeploymentLog(await File.ReadAllTextAsync(logPath, Encoding.UTF8));

            AppendDeploymentLog(await output);
            AppendDeploymentLog(await error);

            AppendDeploymentLog(
                process.ExitCode == 0
                    ? "Package update finished successfully."
                    : $"Package update FAILED: exit code {process.ExitCode}. Проверьте журнал выше.");
        }
        catch (Exception ex)
        {
            AppendDeploymentLog($"Package update failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _packageUpdateRunning = false;
            IsEnabled = true;
            await RefreshAllAsync();
        }
    }

    /// <summary>
    /// Кодирует строку как одинарный литерал PowerShell,
    /// включая апострофы в пути.
    /// </summary>
    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Не даёт закрыть Maintenance во время обновления или отката.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_packageUpdateRunning)
        {
            e.Cancel = true;

            MessageBox.Show(
                this,
                "Дождитесь завершения обновления или отката.",
                "TechMES",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        base.OnClosing(e);
    }
}