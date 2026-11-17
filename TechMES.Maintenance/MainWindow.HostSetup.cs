using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using TechMES.Contracts.Host;
using TechMES.Host.Control;
using TechMES.Maintenance.Services;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance;

/// <summary>Локальный профиль Host, подготовка сертификата и взаимная проверка двух узлов.</summary>
public partial class MainWindow
{
    public HostSetupViewModel HostSetup { get; } = new();

    private string? _hostSetupOperationPath;
    private string HostSetupPath => _hostSetupOperationPath ?? Path.Combine(HostServiceInstallRoot, "Host", "appsettings.json");

    /// <summary>Общий JSON Host: читаемый Unicode в отчётах Maintenance.</summary>
    private static readonly JsonSerializerOptions HostJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

    /// <summary>Общий обработчик исключений; блокирует параллельное локальное развёртывание.</summary>
    private async Task RunHostSetupAsync(Func<Task> action)
    {
        if (HostSetup.Busy || _deploymentOperationRunning) return;
        HostSetup.Busy = true;
        _deploymentOperationRunning = true;
        try { _hostSetupOperationPath = Path.Combine(HostServiceInstallRoot, "Host", "appsettings.json"); await action(); }
        catch (Exception ex) { HostSetup.Report = ex.Message; AppendDiagnostics("Host setup: " + ex); }
        finally { _hostSetupOperationPath = null; HostSetup.Busy = false; _deploymentOperationRunning = false; }
    }

    /// <summary>Проверка прав применяется только к сохранению, сертификату и Firewall.</summary>
    private static void RequireHostAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Запустите Maintenance от администратора.");
    }

    /// <summary>Читает установленный JSONC и сохраняет остальные разделы при редактировании.</summary>
    private JsonObject ReadHostSetup() => JsonNode.Parse(File.ReadAllText(HostSetupPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? throw new InvalidDataException("Пустой appsettings Host. Сначала установите службу через Services.");
    private HttpClient LocalHostSetupClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{HostSetup.HealthPort}/"), Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Загружает профиль и свежий список интерфейсов установленного Host.</summary>
    private async void OnLoadHostSetupClick(object sender, RoutedEventArgs e) => await RunHostSetupAsync(async () =>
    {
        var root = ReadHostSetup();

        HostSetup.HealthPort = root["HostService"]?["Port"]?.GetValue<int>() ?? 5116;

        var shared = root["HostSharedAddress"]?.Deserialize<HostSharedAddressSettings>(HostJson) ?? new();

        HostSetup.Control = root["HostControl"]?.Deserialize<HostControlSettings>(HostJson) ?? new();
        HostSetup.WebFailover = root["HostWebFailover"]?.Deserialize<HostWebFailoverSettings>(HostJson) ?? new();

        using var client = LocalHostSetupClient();

        var network = await client.GetFromJsonAsync<HostNetworkSnapshot>("api/host/network", HostJson) ?? throw new InvalidDataException("Пустой ответ Host.");

        HostSetup.Adapters.Clear();

        foreach (var adapter in network.Adapters)
            HostSetup.Adapters.Add(adapter);

        HostSetup.Shared = shared;
        HostSetup.Report = JsonSerializer.Serialize(network, HostJson);
    });

    /// <summary>Проверяет редактируемый профиль в работающем Host без записи файлов и изменения IP.</summary>
    private async Task<HostNetworkCheck> CheckHostSetupAsync()
    {
        HostSetup.ApplyNumericInputs();
        using var client = LocalHostSetupClient();
        using var response = await client.PostAsJsonAsync("api/host/network/check", HostSetup.Shared, HostJson);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostNetworkCheck>(HostJson) ?? throw new InvalidDataException("Пустая проверка Host.");
    }
    /// <summary>Выводит результаты проверки редактируемого профиля.</summary>
    private async void OnCheckHostSetupClick(object sender, RoutedEventArgs e) => await RunHostSetupAsync(async () => HostSetup.Report = JsonSerializer.Serialize(await CheckHostSetupAsync(), HostJson));

    /// <summary>Создаёт непереносимый приватный ключ в LocalMachine/My; выводит только публичный SHA256.</summary>
    private async void OnCreateHostCertificateClick(object sender, RoutedEventArgs e) => await RunHostSetupAsync(async () =>
    {
        RequireHostAdministrator();
        HostSetup.ApplyNumericInputs();
        if (!HostNetworkPolicy.TryAddress(HostSetup.Shared.NodeAddress, out var node)) throw new InvalidOperationException("Сначала укажите собственный IPv4.");
        if (HostSetup.Control.CertificateSha256.Length > 0)
        {
            using var current = HostNodeCertificates.LoadLocal(HostSetup.Control.CertificateSha256, node.ToString());
            HostSetup.Report = "Существующий сертификат узла действителен. Его SHA256 не изменён.";
            return;
        }
        var script = HostSetupScripts.Certificate(node.ToString());
        var result = await RunHostPowerShellAsync(script);
        var pin = result.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(pin, "^[0-9A-F]{64}$")) throw new InvalidDataException("Не получен SHA256 сертификата Host.");
        HostSetup.Control = new() { Enabled = HostSetup.Control.Enabled, Port = HostSetup.Control.Port, CertificateSha256 = pin, PeerCertificateSha256 = HostSetup.Control.PeerCertificateSha256 };
        HostSetup.Report = "Создан сертификат узла. Скопируйте его SHA256 в поле второго узла на другом компьютере: " + pin;
    });

    /// <summary>Сохраняет сеть, mTLS и автоматику WEB; восстанавливает исходный файл при неудачном запуске.</summary>
    private async void OnSaveHostSetupClick(object sender, RoutedEventArgs e) => await RunHostSetupAsync(async () =>
    {
        RequireHostAdministrator();

        var check = await CheckHostSetupAsync();

        if (!check.LocalChecksPassed)
            throw new InvalidOperationException(string.Join(Environment.NewLine, check.Errors));

        HostSetup.Control.Validate(HostSetup.HealthPort);
        HostSetup.WebFailover.Validate();

        if (HostSetup.WebFailover.Enabled && !HostSetup.Control.Enabled)
            throw new InvalidOperationException("Для автоматической передачи WEB включите защищённый endpoint Host.");

        if (HostSetup.Control.Enabled)
        {
            using var certificate = HostNodeCertificates.LoadLocal(HostSetup.Control.CertificateSha256, HostSetup.Shared.NodeAddress);
        }

        var original = File.ReadAllBytes(HostSetupPath);
        var root = ReadHostSetup();

        root["HostSharedAddress"] = JsonSerializer.SerializeToNode(HostSetup.Shared);
        root["HostControl"] = JsonSerializer.SerializeToNode(HostSetup.Control);
        root["HostWebFailover"] = JsonSerializer.SerializeToNode(HostSetup.WebFailover);

        var backup = HostSetupPath + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + Guid.NewGuid().ToString("N")[..6] + ".bak";

        File.WriteAllBytes(backup, original);

        try
        {
            File.WriteAllText(HostSetupPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            await RestartAndCheckHostAsync();
        }
        catch (Exception failure)
        {
            File.WriteAllBytes(HostSetupPath, original);

            try
            {
                await RestartAndCheckHostAsync();
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException("Host не запустился; конфигурация восстановлена, но запуск после отката тоже не подтверждён: " + rollback.Message, failure);
            }

            throw new InvalidOperationException("Host не запустился с новым профилем; прежняя конфигурация восстановлена. " + failure.Message, failure);
        }

        HostSetup.Report = "Профиль сохранён, новый процесс Host подтверждён. Проверьте состояние IP. Резервная копия: " + backup;
    });

    /// <summary>Подтверждает ответ именно нового процесса службы после перезапуска.</summary>
    private async Task RestartAndCheckHostAsync()
    {
        // Ожидаем SCM Stopped/Running, чтобы не полагаться на фиксированную паузу между stop/start.
        var processIdText = await RunHostPowerShellAsync(HostSetupScripts.Restart());
        if (!int.TryParse(processIdText.Trim(), out var processId) || processId <= 0) throw new InvalidOperationException("Не получен PID запущенной службы Host.");
        using var client = LocalHostSetupClient();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var status = await client.GetFromJsonAsync<JsonElement>("api/host/status");
                if (status.GetProperty("service").GetString() == "TechMES.Host" && status.GetProperty("processId").GetInt32() == processId) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Локальный API Host не подтвердил запуск.");
    }

    /// <summary>Открывает постоянное правило только для сохранённого адреса и двух компьютеров Host.</summary>
    private async void OnOpenHostFirewallClick(object sender, RoutedEventArgs e) => await RunHostSetupAsync(async () =>
    {
        RequireHostAdministrator();
        var root = ReadHostSetup();
        var control = root["HostControl"]?.Deserialize<HostControlSettings>(HostJson) ?? new();
        var shared = root["HostSharedAddress"]?.Deserialize<HostSharedAddressSettings>(HostJson) ?? new();
        control.Validate(HostSetup.HealthPort);
        if (!control.Enabled || !HostNetworkPolicy.TryAddress(shared.NodeAddress, out var node) || !HostNetworkPolicy.TryAddress(shared.PeerAddress, out var peer)) throw new InvalidOperationException("Сначала сохраните включённый HostControl с адресами двух узлов.");
        var command = HostSetupScripts.Firewall(shared, control, HostSetup.HealthPort);
        await RunHostPowerShellAsync(command);
        HostSetup.Report = $"Постоянное правило Host настроено: TCP {control.Port}, только {node} и {peer}.";
    });

    /// <summary>Проверяет второй узел с клиентским сертификатом и pin сервера; сравнивает профиль пары.</summary>
    private async void OnCheckPeerHostClick(object sender, RoutedEventArgs e) => await RunHostSetupAsync(async () =>
    {
        var root = ReadHostSetup();
        var control = root["HostControl"]?.Deserialize<HostControlSettings>(HostJson) ?? new();
        var shared = root["HostSharedAddress"]?.Deserialize<HostSharedAddressSettings>(HostJson) ?? new();
        control.Validate(HostSetup.HealthPort);
        if (!control.Enabled || !HostNetworkPolicy.TryAddress(shared.PeerAddress, out var peer)) throw new InvalidOperationException("Сначала сохраните включённый HostControl.");
        using var client = HostNodeCertificates.CreateClient(control, shared.NodeAddress);
        var url = new UriBuilder("https", peer.ToString(), control.Port, "/api/host/shared-address/status").Uri;
        var report = await client.GetFromJsonAsync<HostNetworkCheck>(url, HostJson) ?? throw new InvalidDataException("Пустой ответ второго Host.");
        var remote = report.Settings;
        if (remote.NodeAddress != shared.PeerAddress || remote.PeerAddress != shared.NodeAddress || remote.VirtualAddress != shared.VirtualAddress || remote.PrefixLength != shared.PrefixLength) throw new InvalidOperationException("Профили узлов не согласованы: проверьте адреса, общий IP и префикс.");
        HostSetup.Report = JsonSerializer.Serialize(report, HostJson);
    });

    /// <summary>Запускает ограниченный по времени PowerShell с UTF-8 выводом.</summary>
    private static async Task<string> RunHostPowerShellAsync(string script)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script));
        var result = await new ProcessRunner().RunAsync(executable, ["-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0) throw new InvalidOperationException(result.CombinedOutput);
        return result.StandardOutput;
    }
}
