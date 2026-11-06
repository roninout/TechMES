using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance;

/// <summary>
/// Обновляет Secondary одной кнопкой из Maintenance на Primary.
/// </summary>
public partial class MainWindow
{
    private static readonly JsonSerializerOptions SecondaryJsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private static string SecondaryProfilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "Maintenance", "secondary-deployment.json");
    private string _secondaryDeploymentLog = "";

    /// <summary>
    /// Загруженный профиль отдельной вкладки Deployment.
    /// </summary>
    public SecondaryDeploymentOptions SecondaryDeployment { get; } = LoadSecondaryDeployment();

    /// <summary>
    /// Журнал операции, обновляемый во время работы.
    /// </summary>
    public string SecondaryDeploymentLog
    {
        get => _secondaryDeploymentLog;
        private set
        {
            _secondaryDeploymentLog = value;
            OnPropertyChanged(nameof(SecondaryDeploymentLog));
        }
    }

    /// <summary>
    /// Читает профиль; повреждение требует исправления и явного сохранения формы.
    /// </summary>
    private static SecondaryDeploymentOptions LoadSecondaryDeployment()
    {
        try
        {
            return File.Exists(SecondaryProfilePath) ? JsonSerializer.Deserialize<SecondaryDeploymentOptions>(File.ReadAllText(SecondaryProfilePath), SecondaryJsonOptions) ?? throw new InvalidDataException("Пустой профиль.") : new();
        }
        catch (Exception ex)
        {
            return new() { LoadError = "Не удалось прочитать профиль: " + ex.Message };
        }
    }

    /// <summary>
    /// Выбирает готовый ZIP, не требуя исходников на серверах.
    /// </summary>
    private void OnChooseSecondaryArchiveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Пакет обновления Secondary", Filter = "TechMES ZIP (*.zip)|*.zip", CheckFileExists = true };

        if (dialog.ShowDialog(this) != true)
            return;

        SecondaryDeployment.ArchivePath = dialog.FileName;
        OnPropertyChanged(nameof(SecondaryDeployment));
    }

    /// <summary>
    /// Сохраняет профиль доставки независимо от обновления самой Maintenance.
    /// </summary>
    private void SaveSecondaryDeployment()
    {
        SecondaryDeployment.Validate(true);
        Directory.CreateDirectory(Path.GetDirectoryName(SecondaryProfilePath)!);
        SecondaryDeployment.LoadError = "";

        File.WriteAllText(SecondaryProfilePath, JsonSerializer.Serialize(SecondaryDeployment, SecondaryJsonOptions), new UTF8Encoding(false));
        OnPropertyChanged(nameof(SecondaryDeployment));
    }

    /// <summary>
    /// Сохраняет поля формы по отдельной кнопке.
    /// </summary>
    private void OnSaveSecondaryDeploymentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSecondaryDeployment();
            SecondaryDeploymentLog = "Профиль сохранён: " + SecondaryProfilePath;
        }
        catch (Exception ex)
        {
            SecondaryDeploymentLog = ex.Message;
            MessageBox.Show(this, ex.Message, "Deployment", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Читает журнал с разрешением одновременной записи из PowerShell.
    /// Временная ошибка чтения не прерывает выполняющийся процесс.
    /// </summary>
    private static async Task<string> ReadSecondaryLogAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "";

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return await reader.ReadToEndAsync();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// Выполняет проверку, доставку, обновление и запуск выбранных служб Secondary.
    /// </summary>
    private async void OnDeploySecondaryClick(object sender, RoutedEventArgs e)
    {
        if (_packageUpdateRunning)
            return;

        string? logPath = null;

        try
        {
            SecondaryDeployment.Validate();
            SaveSecondaryDeployment();

            var script = Path.Combine(AppContext.BaseDirectory, "Tools", "Deploy-TechMESSecondary.ps1");

            if (!File.Exists(script))
                throw new FileNotFoundException("В Maintenance/Tools нет Deploy-TechMESSecondary.ps1. Обновите всю папку Maintenance.", script);

            var logs = Path.Combine(Path.GetDirectoryName(SecondaryProfilePath)!, "UpdateLogs");
            Directory.CreateDirectory(logs);

            logPath = Path.Combine(logs, $"secondary-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            File.WriteAllText(logPath, "", new UTF8Encoding(false));

            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };

            foreach (var argument in SecondaryDeployment.GetArguments(script, logPath))
                info.ArgumentList.Add(argument);

            _packageUpdateRunning = true;
            IsEnabled = false;
            SecondaryDeploymentLog = "Проверка доступа к Secondary...";

            using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var completion = process.WaitForExitAsync();

            while (!completion.IsCompleted)
            {
                var text = await ReadSecondaryLogAsync(logPath);

                if (text.Length != 0)
                    SecondaryDeploymentLog = text;

                await Task.WhenAny(completion, Task.Delay(1000));
            }

            await completion;
            SecondaryDeploymentLog = await ReadSecondaryLogAsync(logPath);

            var stdout = await output;
            var stderr = await error;

            if (process.ExitCode != 0)
            {
                if (SecondaryDeploymentLog.Length == 0)
                    SecondaryDeploymentLog = stdout + Environment.NewLine + stderr;

                throw new InvalidOperationException("Deploy Secondary не подтверждён. См. журнал: " + logPath + ". Если задача уже запущена, она продолжает работать на Secondary; проверьте указанный в журнале status.json перед повтором.");
            }

            SecondaryDeploymentLog += Environment.NewLine + "Готово: выбранные службы Secondary запущены.";
        }
        catch (Exception ex)
        {
            SecondaryDeploymentLog += Environment.NewLine + ex.Message;
            MessageBox.Show(this, ex.Message, "Deploy Secondary", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _packageUpdateRunning = false;
            IsEnabled = true;
        }
    }
}