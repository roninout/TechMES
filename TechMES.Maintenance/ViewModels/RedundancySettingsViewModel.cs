using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TechMES.Contracts.Runtime;

namespace TechMES.Maintenance.ViewModels;

/// <summary>Настройки владения Runtime и проверки его доступа к локальному PostgreSQL.</summary>
public sealed class RedundancySettingsViewModel : ObservableObject
{
    private string _nodeId = Environment.MachineName, _role = "Primary", _scope = "TechMES.Runtime";
    private bool _coordinationEnabled, _startInStandby, _localEnabled, _isChecking;
    private int _failureThreshold = 3, _recoveryThreshold = 3, _port = 5432;
    private string _serviceName = "postgresql-x64-18", _binDirectory = @"D:\PostgreSQL\18\bin", _dataDirectory = @"D:\PostgreSQL\18\data";
    private string _settingsPath = "", _diagnostics = "Проверка ещё не выполнялась.";

    public IReadOnlyList<string> Roles { get; } = ["Primary", "Secondary"];

    public string NodeId { get => _nodeId; set => SetProperty(ref _nodeId, value); }
    public string Role { get => _role; set => SetProperty(ref _role, value); }
    public string Scope { get => _scope; set => SetProperty(ref _scope, value); }
    public bool CoordinationEnabled { get => _coordinationEnabled; set => SetProperty(ref _coordinationEnabled, value); }
    public bool StartInStandby { get => _startInStandby; set => SetProperty(ref _startInStandby, value); }
    public int FailureThreshold { get => _failureThreshold; set => SetProperty(ref _failureThreshold, value); }
    public int RecoveryThreshold { get => _recoveryThreshold; set => SetProperty(ref _recoveryThreshold, value); }
    public bool LocalEnabled { get => _localEnabled; set => SetProperty(ref _localEnabled, value); }
    public string ServiceName { get => _serviceName; set => SetProperty(ref _serviceName, value); }
    public string BinDirectory { get => _binDirectory; set => SetProperty(ref _binDirectory, value); }
    public string DataDirectory { get => _dataDirectory; set => SetProperty(ref _dataDirectory, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string SettingsPath { get => _settingsPath; private set => SetProperty(ref _settingsPath, value); }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public bool IsChecking { get => _isChecking; private set => SetProperty(ref _isChecking, value); }

    /// <summary>Читает только настройки своей вкладки из выбранного рабочего файла.</summary>
    public void Load(JsonObject root, string path)
    {
        SettingsPath = path;
        NodeId = Read(root, Environment.MachineName, "Runtime", "NodeId");
        StartInStandby = Read(root, false, "Runtime", "StartInStandby");
        CoordinationEnabled = Read(root, false, "Runtime", "Redundancy", "Enabled");
        Role = Read(root, "Primary", "Runtime", "Redundancy", "Role");
        Scope = Read(root, "TechMES.Runtime", "Runtime", "Redundancy", "Scope");
        FailureThreshold = Read(root, 3, "Runtime", "Redundancy", "FailureThreshold");
        RecoveryThreshold = Read(root, 3, "Runtime", "Redundancy", "RecoveryThreshold");

        LocalEnabled = Read(root, false, PostgreSqlControlOptions.SectionName, "Enabled");
        ServiceName = Read(root, "postgresql-x64-18", PostgreSqlControlOptions.SectionName, "ServiceName");
        BinDirectory = Read(root, @"D:\PostgreSQL\18\bin", PostgreSqlControlOptions.SectionName, "BinDirectory");
        DataDirectory = Read(root, @"D:\PostgreSQL\18\data", PostgreSqlControlOptions.SectionName, "DataDirectory");
        Port = Read(root, 5432, PostgreSqlControlOptions.SectionName, "Port");

        Diagnostics = "Настройки загружены. После изменения сохраните их и перезапустите Runtime.";
    }

    /// <summary>Проверяет значения до записи любого appsettings через общую кнопку Save.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(NodeId) || NodeId.Trim().Length > 100)
            throw new InvalidOperationException("Runtime Node ID: от 1 до 100 символов.");

        if (Role is not ("Primary" or "Secondary"))
            throw new InvalidOperationException("Выберите роль Runtime Primary или Secondary.");

        if (string.IsNullOrWhiteSpace(Scope) || Scope.Trim().Length > 100)
            throw new InvalidOperationException("Runtime Scope: от 1 до 100 символов.");

        if (FailureThreshold is < 1 or > 10 || RecoveryThreshold is < 1 or > 10)
            throw new InvalidOperationException("Количество попыток: от 1 до 10.");

        if (LocalEnabled && (!Regex.IsMatch(ServiceName.Trim(), "^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(BinDirectory.Trim(), @"^[A-Za-z]:[\\/]") || !Regex.IsMatch(DataDirectory.Trim(), @"^[A-Za-z]:[\\/]") || Port is < 1 or > 65535))
            throw new InvalidOperationException("Проверьте имя службы PostgreSQL, локальные абсолютные пути bin/data и порт.");
    }

    /// <summary>Обновляет известные поля, сохраняя строки подключения и незнакомые параметры.</summary>
    public void Apply(JsonObject root)
    {
        Validate();

        var runtime = Child(root, "Runtime");

        runtime["NodeId"] = NodeId.Trim();
        runtime["StartInStandby"] = StartInStandby;

        var redundancy = Child(runtime, "Redundancy");

        redundancy["Enabled"] = CoordinationEnabled;
        redundancy["Role"] = Role;
        redundancy["Scope"] = Scope.Trim();
        redundancy["FailureThreshold"] = FailureThreshold;
        redundancy["RecoveryThreshold"] = RecoveryThreshold;

        var local = Child(root, PostgreSqlControlOptions.SectionName);

        local["Enabled"] = LocalEnabled;
        local["ServiceName"] = ServiceName.Trim();
        local["BinDirectory"] = BinDirectory.Trim();
        local["DataDirectory"] = DataDirectory.Trim();
        local["Port"] = Port;
    }

    /// <summary>Читает готовые снимки Runtime; запрос не запускает и не останавливает PostgreSQL.</summary>
    public async Task CheckAsync(string baseUrl)
    {
        if (IsChecking)
            return;

        IsChecking = true;
        Diagnostics = "Проверка Runtime...";

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(8)
            };

            var localTask = client.GetFromJsonAsync<PostgreSqlLocalHealthResponse>("api/database/local/health");
            var runtimeTask = client.GetFromJsonAsync<RuntimeActivityStatusResponse>("api/runtime/status");

            await Task.WhenAll(localTask, runtimeTask);

            var local = await localTask ?? throw new InvalidOperationException("Empty local status.");
            var runtime = await runtimeTask ?? throw new InvalidOperationException("Empty Runtime status.");
            var service = local.Service;

            var expected = new PostgreSqlControlOptions
            {
                Enabled = LocalEnabled,
                ServiceName = ServiceName.Trim(),
                BinDirectory = BinDirectory.Trim(),
                DataDirectory = DataDirectory.Trim(),
                Port = Port
            };

            var current = local.CheckedAtUtc is not null &&
                DateTimeOffset.UtcNow - local.CheckedAtUtc.Value <= TimeSpan.FromSeconds(Math.Max(5, local.HealthPeriodSeconds) * 3d);

            var ready = current && local.Settings == expected && local.Settings.Enabled &&
                local.PermissionsOk && service?.ManagedByService == true;

            Diagnostics = $"{(ready ? "Доступ подтверждён" : "Доступ не подтверждён")}\nRuntime: {runtime.NodeId}; mode: {runtime.Mode}\nУчётная запись Runtime: {local.RuntimeIdentity ?? "не определена"}\nПрава Query/Start/Stop: {local.PermissionsOk}; Windows error: {local.WindowsError}\nСлужба: {service?.ServiceName}; состояние: {service?.ServiceState}\nService PID: {service?.ServiceProcessId}; PostgreSQL PID: {service?.PostmasterProcessId}\nManagedByService: {service?.ManagedByService}\n{local.Message}";

            if (!current)
                Diagnostics += "\nДиагностика ещё не получена или устарела.";

            if (local.Settings != expected)
                Diagnostics += "\nФорма отличается от настроек работающего Runtime: сохраните и перезапустите Runtime.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Diagnostics = "Этот Runtime ещё не содержит проверку локального PostgreSQL. Обновите Runtime новым пакетом.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            Diagnostics = "Проверка доступна с компьютера Runtime. Запустите Maintenance локально на этом сервере.";
        }
        catch (Exception ex)
        {
            Diagnostics = $"Проверка не выполнена ({ex.GetType().Name}). Проверьте запуск Runtime и его локальный адрес.";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Возвращает вложенный объект, не заменяя существующие данные другого типа.</summary>
    private static JsonObject Child(JsonObject root, string name)
    {
        if (root[name] is null)
            root[name] = new JsonObject();

        return root[name] as JsonObject
            ?? throw new InvalidOperationException($"Некорректный объект JSON: {name}.");
    }

    /// <summary>Читает необязательное поле и применяет значение по умолчанию.</summary>
    private static T Read<T>(JsonObject root, T fallback, params string[] path)
    {
        try
        {
            JsonNode? node = root;

            foreach (var key in path)
                node = node?[key];

            return node is null ? fallback : node.GetValue<T>();
        }
        catch
        {
            return fallback;
        }
    }
}