using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Contracts.Runtime;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

public sealed record PostgreSqlFailbackSetup(string NodeAddress, string PeerAddress, string PreferredPrimary, string SystemIdentifier, bool Enabled);

/// <summary>Результат локальной подготовки; импортированный отчёт остаётся снимком на указанное время.</summary>
public sealed class PostgreSqlHostSetupReport
{
    public int Format { get; set; }
    public string ProfileFingerprint { get; set; } = "";
    public Guid RunId { get; set; }
    public string Computer { get; set; } = "";
    public string[] Addresses { get; set; } = [];
    public DateTimeOffset CheckedAtUtc { get; set; }
    public bool Ready { get; set; }
    public bool PreparationCompleted { get; set; }
    public string HostKind { get; set; } = "";
    public string HostService { get; set; } = "";
    public string HostServiceState { get; set; } = "";
    public int HostProcessId { get; set; }
    public string SettingsPath { get; set; } = "";
    public PostgreSqlLocalHealthResponse? Local { get; set; }
    public PostgreSqlFailbackSetup? Failback { get; set; }
    public string Message { get; set; } = "";
    public string LogPath { get; set; } = "";
}

/// <summary>Готовит локальный запуск и переносимый комплект из штатного установщика.</summary>
public sealed class PostgreSqlHostSetupService
{
    public const string ProfileMarker = "$script:EmbeddedProfileBase64 = ''";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>Настройки Agent не изменяют отпечаток существующего отчёта AdoptService.</summary>
    public static string Fingerprint(PostgreSqlSetupProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, Json))));

    /// <summary>Создаёт новый каталог; чужие файлы и прежние комплекты не заменяются.</summary>
    public string Export(PostgreSqlSetupProfile profile, string outputDirectory, string toolsDirectory, bool includePackage = true, PostgreSqlFailbackSetup? failback = null)
    {
        profile.ValidateHost();
        var directory = Path.Combine(outputDirectory, "TechMES_PgHost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var tools = Path.Combine(directory, "Tools");
            Directory.CreateDirectory(tools);

            foreach (var name in new[] { "Install-TechMESPostgreSqlHost.ps1", "Set-PostgreSQL18ServiceControl.ps1" })
                File.Copy(Path.Combine(toolsDirectory, name), Path.Combine(tools, name));

            var engine = File.ReadAllText(Path.Combine(toolsDirectory, "Prepare-TechMESPostgreSqlController.ps1"), Encoding.UTF8);

            if (engine.Split(ProfileMarker, StringSplitOptions.None).Length != 2)
                throw new InvalidDataException("Неподходящая версия инструмента подготовки контроллера.");

            var payload = JsonSerializer.Serialize(new { Profile = profile, ProfileFingerprint = Fingerprint(profile), Failback = failback }, Json);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            engine = engine.Replace(ProfileMarker, "$script:EmbeddedProfileBase64 = '" + encoded + "'", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, "Prepare-Controller.ps1"), engine, new UTF8Encoding(true));

            if (includePackage && profile.HostMode != "Runtime" && !string.IsNullOrWhiteSpace(profile.AgentPackageDirectory))
                CopyAgentPackage(profile.AgentPackageDirectory, directory);

            return directory;
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    /// <summary>Копирует только перечисленные в manifest файлы и проверяет SHA256 уже скопированных байтов.</summary>
    private static void CopyAgentPackage(string source, string destination)
    {
        source = Path.GetFullPath(source);
        RejectLinks(source);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "manifest.json"), Encoding.UTF8));
        var root = manifest.RootElement;

        if (root.GetProperty("Format").GetInt32() != 1 || root.GetProperty("Runtime").GetString() != "win-x64" || !root.GetProperty("SelfContained").GetBoolean())
            throw new InvalidDataException("Выберите распакованный автономный пакет Agent win-x64 с manifest.json.");

        var components = root.GetProperty("Components").EnumerateArray().ToArray();

        if (components.Length != 1 || components[0].GetProperty("Key").GetString() != "postgres-agent" || components[0].GetProperty("Folder").GetString() != "PostgreSql.Agent" || components[0].GetProperty("Service").GetString() != "TechMES.PostgreSql.Agent" || components[0].GetProperty("Exe").GetString() != "TechMES.PostgreSql.Agent.exe")
            throw new InvalidDataException("В выбранном каталоге нет отдельного пакета PostgreSQL Agent.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in components[0].GetProperty("Files").EnumerateArray())
        {
            var name = (file.GetProperty("Path").GetString() ?? "").Replace('\\', '/');
            var hash = file.GetProperty("Sha256").GetString() ?? "";

            if (name.Length == 0 || name.Any(char.IsControl) || name.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) || !names.Add(name) || !Regex.IsMatch(hash, "^[A-Fa-f0-9]{64}$"))
                throw new InvalidDataException("Некорректный путь или SHA256 в manifest Agent: " + name);

            var input = Path.Combine(source, "PostgreSql.Agent", name);
            RejectLinks(input);
            var output = Path.Combine(destination, "PostgreSql.Agent", name);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(input, output);

            using var stream = File.OpenRead(output);

            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Не совпадает SHA256 файла Agent: " + name);
        }

        foreach (var required in new[] { "TechMES.PostgreSql.Agent.exe", "TechMES.PostgreSql.Control.dll", "Tools/Install-TechMESPostgreSqlHost.ps1", "Tools/Set-PostgreSQL18ServiceControl.ps1" })
            if (!names.Contains(required))
                throw new InvalidDataException("В пакете Agent отсутствует: " + required);

        File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(destination, "agent-manifest.json"));
    }

    /// <summary>Пакет не должен читать файлы через ссылки в любом существующем родителе.</summary>
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки в пути пакета не поддерживаются: " + current);
    }

    /// <summary>Читает снимок только для выбранного компьютера и точной версии профиля.</summary>
    public PostgreSqlHostSetupReport ReadReport(PostgreSqlSetupProfile profile, string path, Guid? runId = null)
    {
        profile.ValidateHost();
        var report = JsonSerializer.Deserialize<PostgreSqlHostSetupReport>(File.ReadAllText(path, Encoding.UTF8), Json) ?? throw new InvalidDataException("Пустой отчёт контроллера.");

        if (report.Format != 1 || report.ProfileFingerprint != Fingerprint(profile) || report.RunId == Guid.Empty || (runId is not null && report.RunId != runId) || report.CheckedAtUtc == default || report.CheckedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) || string.IsNullOrWhiteSpace(report.Computer) || report.Addresses is null || (!profile.ComputerName.Equals(report.Computer, StringComparison.OrdinalIgnoreCase) && !report.Addresses.Contains(profile.ComputerName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Отчёт относится к другому компьютеру, запуску или версии профиля.");

        if (report.Ready)
        {
            var local = report.Local;
            var expectedService = report.HostKind == "Runtime" ? "TechMES.Runtime.Service" : "TechMES.PostgreSql.Agent";

            if (local?.Service is not { } service || local.Settings is null || local.CheckedAtUtc is not { } checkedAt)
                throw new InvalidDataException("В успешном отчёте нет снимка контроллера или службы PostgreSQL.");

            var validHost = report.HostKind is "Runtime" or "Standalone" && (profile.HostMode == "Auto" || profile.HostMode == report.HostKind) && report.HostService == expectedService && report.HostServiceState == "Running" && report.HostProcessId > 0;
            var validSnapshot = local.OwnsController && local.MonitoringEnabled && local.PermissionsOk && local.ControllerState == "Monitoring" && local.HostKind == report.HostKind && checkedAt <= report.CheckedAtUtc.AddSeconds(1) && report.CheckedAtUtc - checkedAt <= TimeSpan.FromSeconds(Math.Max(30, local.HealthPeriodSeconds * 3));
            var validSettings = string.Equals(local.Settings.ServiceName, profile.ServiceName, StringComparison.OrdinalIgnoreCase) && SamePath(local.Settings.BinDirectory, profile.BinDirectory) && SamePath(local.Settings.DataDirectory, profile.DataDirectory) && local.Settings.Port == profile.Port;
            var validPostgreSql = local.PostgreSqlRole is "Primary" or "Standby" && service.ManagedByService && service.ServiceState == "Running" && string.Equals(service.Computer, report.Computer, StringComparison.OrdinalIgnoreCase) && string.Equals(service.ServiceName, profile.ServiceName, StringComparison.OrdinalIgnoreCase) && SamePath(service.DataDirectory, profile.DataDirectory) && service.Port == profile.Port && service.PostmasterProcessId > 0 && service.ServiceProcessId > 0 && service.ListenerProcessId == service.PostmasterProcessId;

            if (!validHost || !validSnapshot || !validSettings || !validPostgreSql)
                throw new InvalidDataException("Отчёт содержит противоречивое состояние контроллера PostgreSQL.");

            if (report.Failback is { } policy && (!report.PreparationCompleted || !report.Addresses.Contains(policy.NodeAddress)
                || local.SystemIdentifier != policy.SystemIdentifier || (policy.Enabled && policy.NodeAddress == policy.PreferredPrimary)))
                throw new InvalidDataException("Отчёт об автовозврате относится к другой паре или узлу.");
        }

        return report;
    }

    /// <summary>Сравнивает пути Windows независимо от регистра и вида разделителя.</summary>
    private static bool SamePath(string? left, string right) => string.Equals(left?.Replace('/', '\\').TrimEnd('\\'), right.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Запускает автономную консоль с UAC и ждёт её результата.</summary>
    public async Task<PostgreSqlHostSetupReport> RunLocalAsync(PostgreSqlSetupProfile profile, string toolsDirectory, bool prepare, bool includePackage = true, PostgreSqlFailbackSetup? failback = null)
    {
        if (!PostgreSqlSetupService.IsLocal(profile))
            throw new InvalidOperationException("Выбран другой компьютер. Экспортируйте комплект и запустите его на сервере БД.");

        var directory = await Task.Run(() => Export(profile, Path.Combine(PostgreSqlSetupService.Root, "ControllerRuns"), toolsDirectory, prepare && includePackage, failback));
        var mode = failback is not null ? "Failback" : prepare ? "Prepare" : "Check";
        var id = Guid.NewGuid();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas"
        };

        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory, "Prepare-Controller.ps1"), "-Mode", mode, "-RunId", id.ToString(), "-NoPause" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить подготовку контроллера.");
        await process.WaitForExitAsync();

        var report = ReadReport(profile, Path.Combine(directory, "Runs", id.ToString("N"), "result.json"), id);

        if (process.ExitCode != 0 && report.Ready)
            throw new InvalidDataException("Код завершения не соответствует успешному отчёту контроллера.");

        return report;
    }
}
