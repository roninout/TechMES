using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

/// <summary>Первичная установка Host из исходников либо общего автономного ZIP.</summary>
public partial class MainWindow
{
    private bool _hostServiceBusy;
    private string _hostServiceInstallStatus = "Режим наблюдения. Общий IP не назначается.";
    private string? _hostServiceInstallRoot;

    public bool CanInstallHostService => !_hostServiceBusy && !_deploymentOperationRunning;
    public string HostServiceInstallStatus
    {
        get => _hostServiceInstallStatus;
        private set { _hostServiceInstallStatus = value; OnPropertyChanged(nameof(HostServiceInstallStatus)); }
    }

    /// <summary>На сервере Maintenance/Host устанавливаются рядом; на ПК сборки используется PublishRoot.</summary>
    public string HostServiceInstallRoot
    {
        get => _hostServiceInstallRoot ??= !File.Exists(Path.Combine(_repositoryRoot.FullName, "TechMES.sln")) && new DirectoryInfo(AppContext.BaseDirectory).Name.Equals("Maintenance", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName : _configuration.Deployment.PublishRoot;
        set { _hostServiceInstallRoot = value; OnPropertyChanged(nameof(HostServiceInstallRoot)); OnPropertyChanged(nameof(HostServiceInstallPath)); }
    }
    public string HostServiceInstallPath => "Каталог службы: " + Path.Combine(HostServiceInstallRoot, "Host");

    /// <summary>Собирает только Host или выбирает ZIP. Остальные службы не останавливаются.</summary>
    private async void OnInstallHostServiceClick(object sender, RoutedEventArgs e)
    {
        if (!CanInstallHostService) return;
        _hostServiceBusy = true;
        _deploymentOperationRunning = true;
        OnPropertyChanged(nameof(CanInstallHostService));
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Запустите Maintenance от администратора для установки службы.");

            var definition = GetServiceDefinition("host") ?? throw new InvalidOperationException("Отсутствует описание Host.");
            if (definition.ServiceName != HostServiceDefinition.ServiceName)
                throw new InvalidOperationException("Служба Host должна иметь Windows name TechMES.Host.");
            var installed = await _serviceManager.QueryAsync(definition.ServiceName);
            if (installed.Success)
            {
                if (installed.Status != "RUNNING")
                {
                    var started = await _serviceManager.StartAsync(definition.ServiceName);
                    if (!started.Success) throw new InvalidOperationException(started.Details);
                }
                await FinishHostInstallationAsync(definition);
                return;
            }

            // Проверяем путь до сборки и до создания конфигурации.
            var profile = new ServerDeploymentProfile { Name = "Host", ComputerName = Environment.MachineName, InstallRoot = HostServiceInstallRoot.TrimEnd('\\'), UpdateRuntime = false, UpdateCalc = false, UpdateWeb = false, UpdateHost = true };
            profile.Validate();
            var sourceProject = Path.Combine(_repositoryRoot.FullName, "TechMES.Host", "TechMES.Host.csproj");
            string packageRoot = "", archivePath = "";
            byte[] manifestBytes;
            var canBuild = File.Exists(sourceProject);
            if (canBuild)
            {
                var sdk = await RunDeploymentToolAsync("Build-TechMESUpdate.ps1", ["-SdkOnly"], line => Dispatcher.Invoke(() => AppendDiagnostics(line)));
                canBuild = sdk == 0;
            }
            if (canBuild)
            {
                HostServiceInstallStatus = "Сборка автономного Host. Работающие службы продолжают работу.";
                var outputRoot = Path.Combine(Path.GetTempPath(), "TechMES_HostBuilds");
                var result = await RunDeploymentToolAsync("Build-TechMESUpdate.ps1", ["-RepositoryRoot", _repositoryRoot.FullName, "-OutputRoot", outputRoot, "-HostOnly", "-NoArchive"], line =>
                {
                    Dispatcher.Invoke(() => AppendDiagnostics(line));
                    if (line.StartsWith("PACKAGE_ROOT=", StringComparison.Ordinal)) packageRoot = line[13..].Trim();
                });
                if (result != 0 || string.IsNullOrWhiteSpace(packageRoot)) throw new InvalidOperationException("Сборка Host завершилась ошибкой. Подробности — в Diagnostics.");
                manifestBytes = await File.ReadAllBytesAsync(Path.Combine(packageRoot, "manifest.json"));
            }
            else
            {
                var dialog = new OpenFileDialog { Title = "Выберите новый ZIP TechMES с компонентом Host", Filter = "TechMES ZIP|*.zip", CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) { HostServiceInstallStatus = "Установка отменена. Службы не изменены."; return; }
                archivePath = dialog.FileName;
                using var archive = ZipFile.OpenRead(archivePath);
                var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("В архиве нет manifest.json.");
                if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Слишком большой manifest.json.");
                using var stream = entry.Open();
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                manifestBytes = bytes.ToArray();
            }

            using (var manifest = JsonDocument.Parse(Encoding.UTF8.GetString(manifestBytes).TrimStart('\uFEFF')))
            {
                var root = manifest.RootElement;
                if (root.GetProperty("Format").GetInt32() != 1 || root.GetProperty("Runtime").GetString() != "win-x64" || !root.GetProperty("SelfContained").GetBoolean() || !root.GetProperty("Components").EnumerateArray().Any(part => part.GetProperty("Key").GetString() == "host"))
                    throw new InvalidDataException("Нужен новый автономный ZIP win-x64 с компонентом Host.");
            }

            var settingsPath = Path.Combine(profile.InstallRoot, "Host", "appsettings.json");
            if (!File.Exists(settingsPath))
            {
                var webUrl = GetServiceDefinition("web")?.HealthUrl ?? "http://127.0.0.1:5163/api/health";
                if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var url) || !url.IsLoopback || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
                    throw new InvalidOperationException("Сначала задайте локальный WEB Health URL в настройках Maintenance.");
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                var json = JsonSerializer.Serialize(new { HostService = new { Port = 5116, WebHealthUrl = webUrl, CheckPeriodSeconds = 5, TimeoutSeconds = 3, SharedAddressEnabled = false } }, DeploymentPackageService.JsonOptions);
                using var file = new FileStream(settingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                await writer.WriteAsync(json);
            }

            // Существующий локальный appsettings сохраняется; health-порт берём из него.
            using (var local = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var port = local.RootElement.GetProperty("HostService").TryGetProperty("Port", out var configured) ? configured.GetInt32() : 5116;
                if (port is < 1 or > 65535) throw new InvalidDataException("Некорректный HostService:Port.");
                definition.HealthUrl = $"http://127.0.0.1:{port}/api/health";
            }
            _configurationStore.Save(_configuration);
            foreach (var row in ServiceStatuses) row.RefreshDefinitionBindings();
            HostServiceInstallStatus = "Установка и запуск TechMes Host...";
            var jsonProfile = JsonSerializer.Serialize(new { profile.Name, profile.ComputerName, profile.InstallRoot, profile.RuntimeStatusUrl, Components = new[] { "host" }, ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)), ArchiveName = Path.GetFileName(archivePath) });
            var args = new List<string> { "-ProfileBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonProfile)) };
            if (packageRoot.Length > 0) args.AddRange(["-PackageRoot", packageRoot]);
            else args.AddRange(["-ArchivePath", archivePath]);
            var exitCode = await RunDeploymentToolAsync("Update-TechMESServer.ps1", args, line => Dispatcher.Invoke(() => AppendDiagnostics(line)));
            if (exitCode != 0) throw new InvalidOperationException("Установка Host не завершена. Подробности и результат отката — в Diagnostics.");
            await FinishHostInstallationAsync(definition);
        }
        catch (Exception ex)
        {
            HostServiceInstallStatus = ex.Message;
            AppendDiagnostics("TechMes Host: " + ex);
        }
        finally
        {
            _hostServiceBusy = false;
            _deploymentOperationRunning = false;
            OnPropertyChanged(nameof(CanInstallHostService));
        }
    }

    /// <summary>Ожидает локальный HTTP Host; готовность WEB не является условием установки контроллера.</summary>
    private async Task FinishHostInstallationAsync(ServiceDefinition definition)
    {
        var health = "Not checked";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            health = await _healthProbeService.ProbeAsync(definition.HealthUrl);
            if (health.StartsWith("OK", StringComparison.Ordinal)) break;
            await Task.Delay(500);
        }
        var row = ServiceStatuses.Single(service => service.ServiceName == definition.ServiceName);
        await RefreshServiceAsync(row, true);
        if (!health.StartsWith("OK", StringComparison.Ordinal)) throw new InvalidOperationException("Служба установлена, но локальная диагностика Host пока недоступна: " + health);
        HostServiceInstallStatus = "TechMes Host запущен. Режим наблюдения; общий IP не назначается.";
    }
}
