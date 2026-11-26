using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TechMES.Contracts.Runtime;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

/// <summary>Проверяет переносимые комплекты, привязку отчётов и совместимость подготовки БД.</summary>
public sealed class PostgreSqlHostSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TechMES_PgHostTests_" + Guid.NewGuid().ToString("N"));
    private readonly PostgreSqlHostSetupService _service = new();

    /// <summary>Подготовка Agent не аннулирует ранее сохранённый отчёт AdoptService.</summary>
    [Fact]
    public void RetainsOriginalDatabaseFingerprint()
    {
        var profile = Profile();
        var original = JsonSerializer.Serialize(new { profile.Name, profile.ComputerName, profile.ServiceName, profile.BinDirectory, profile.DataDirectory, profile.Port }, new JsonSerializerOptions { WriteIndented = true });
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));

        profile.HostMode = "Standalone";
        profile.AgentDirectory = @"C:\TechMES\Agent";

        Assert.Equal(expected, PostgreSqlSetupService.Fingerprint(profile));
        var controller = PostgreSqlHostSetupService.Fingerprint(profile);
        profile.HostHealthPeriodSeconds = 15;
        Assert.NotEqual(controller, PostgreSqlHostSetupService.Fingerprint(profile));
    }

    /// <summary>Локальный Runtime и отдельный Agent имеют общий формат подтверждения.</summary>
    [Theory]
    [InlineData("Runtime")]
    [InlineData("Standalone")]
    public void AcceptsFreshMatchingReport(string kind)
    {
        var profile = Profile();
        var report = Report(profile, kind);

        Assert.True(_service.ReadReport(profile, Save(report), report.RunId).Ready);
    }

    /// <summary>Старый, чужой или противоречивый успешный отчёт не подтверждает подготовку.</summary>
    [Theory]
    [InlineData("computer")]
    [InlineData("profile")]
    [InlineData("stale")]
    [InlineData("lease")]
    [InlineData("listener")]
    [InlineData("host-service")]
    [InlineData("host-mode")]
    [InlineData("settings")]
    public void RejectsInvalidReadyReport(string error)
    {
        var profile = Profile();
        var report = Report(profile, "Runtime");
        var local = report.Local!;

        switch (error)
        {
            case "computer": report.Computer = "other"; break;
            case "profile": profile.Port++; break;
            case "stale": report.Local = local with { CheckedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) }; break;
            case "lease": report.Local = local with { OwnsController = false }; break;
            case "listener": report.Local = local with { Service = local.Service! with { ListenerProcessId = 999 } }; break;
            case "host-service": report.HostService = "TechMES.PostgreSql.Agent"; break;
            case "host-mode": profile.HostMode = "Standalone"; report.ProfileFingerprint = PostgreSqlHostSetupService.Fingerprint(profile); break;
            case "settings": report.Local = local with { Settings = null! }; break;
        }

        Assert.Throws<InvalidDataException>(() => _service.ReadReport(profile, Save(report)));
    }

    /// <summary>Локальный запуск не принимает результат другого RunId.</summary>
    [Fact]
    public void RejectsAnotherRun()
    {
        var profile = Profile();
        Assert.Throws<InvalidDataException>(() => _service.ReadReport(profile, Save(Report(profile, "Runtime")), Guid.NewGuid()));
    }

    /// <summary>Отказ подготовки остаётся читаемым отчётом с журналом.</summary>
    [Fact]
    public void ReadsFailedPreparationWithoutClaimingReady()
    {
        var profile = Profile();
        var report = Report(profile, "Runtime");
        report.Ready = false;
        report.Local = null;
        report.PreparationCompleted = true;
        report.Message = "Fresh snapshot was not confirmed.";

        var loaded = _service.ReadReport(profile, Save(report));

        Assert.False(loaded.Ready);
        Assert.True(loaded.PreparationCompleted);
    }

    /// <summary>Комплект содержит проверенные файлы Agent и профиль, встроенный без shell-интерполяции.</summary>
    [Fact]
    public void ExportsVerifiedAgentPackage()
    {
        var profile = Profile();
        profile.AgentPackageDirectory = Package(false);
        var directory = _service.Export(profile, Path.Combine(_root, "export"), Tools());

        Assert.True(File.Exists(Path.Combine(directory, "agent-manifest.json")));
        Assert.True(File.Exists(Path.Combine(directory, "PostgreSql.Agent", "TechMES.PostgreSql.Agent.exe")));
        Assert.DoesNotContain(PostgreSqlHostSetupService.ProfileMarker, File.ReadAllText(Path.Combine(directory, "Prepare-Controller.ps1")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportsFailoverPolicyForTheSelectedNode(bool enabled)
    {
        var policy = new PostgreSqlFailoverSetup("192.168.3.12", "192.168.3.11", "123456", enabled);
        var directory = _service.Export(Profile(), _root, Tools(), includePackage: false, failover: policy);
        var script = File.ReadAllText(Path.Combine(directory, "Prepare-Controller.ps1"));
        var encoded = script.Split('\'')[1];
        using var payload = JsonDocument.Parse(Convert.FromBase64String(encoded));

        Assert.Equal("Failover", PostgreSqlHostSetupService.ControllerMode(true, null, policy));
        Assert.Equal(policy, payload.RootElement.GetProperty("Failover").Deserialize<PostgreSqlFailoverSetup>());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("Failback").ValueKind);
    }

    /// <summary>Подменённые файлы не попадают в готовый комплект; незавершённый каталог удаляется.</summary>
    [Fact]
    public void RejectsTamperedAgentAndCleansOwnOutput()
    {
        var profile = Profile();
        profile.AgentPackageDirectory = Package(true);
        var output = Path.Combine(_root, "export");

        Assert.Throws<InvalidDataException>(() => _service.Export(profile, output, Tools()));
        Assert.Empty(Directory.GetDirectories(output));
    }

    /// <summary>Manifest не может вывести копирование за пределы каталога комплекта.</summary>
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("Tools/../../escape.exe")]
    public void RejectsUnsafePackagePaths(string path)
    {
        var profile = Profile();
        profile.AgentPackageDirectory = Package(false);
        var manifestPath = Path.Combine(profile.AgentPackageDirectory, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["Components"]![0]!["Files"]![0]!["Path"] = path;
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        Assert.Throws<InvalidDataException>(() => _service.Export(profile, Path.Combine(_root, "export"), Tools()));
        Assert.False(File.Exists(Path.Combine(_root, "escape.exe")));
    }

    /// <summary>Проверка установленного Runtime не требует копирования выбранного пакета Agent.</summary>
    [Fact]
    public void CheckBundleDoesNotReadAgentPackage()
    {
        var profile = Profile();
        profile.AgentPackageDirectory = Path.Combine(_root, "absent");

        Assert.True(Directory.Exists(_service.Export(profile, _root, Tools(), false)));
    }

    /// <summary>Подготовка получает явный профиль, не использует адреса из тестового окружения.</summary>
    private static PostgreSqlSetupProfile Profile() => new() { Name = "Database", ComputerName = "server", ServiceName = "postgresql-test", BinDirectory = @"C:\PG\bin", DataDirectory = @"C:\PG\data", Port = 5544 };

    /// <summary>Формирует согласованный снимок контроллера и локального postmaster.</summary>
    private static PostgreSqlHostSetupReport Report(PostgreSqlSetupProfile profile, string kind) => new()
    {
        Format = 1,
        ProfileFingerprint = PostgreSqlHostSetupService.Fingerprint(profile),
        RunId = Guid.NewGuid(),
        Computer = "server",
        CheckedAtUtc = DateTimeOffset.UtcNow,
        Ready = true,
        HostKind = kind,
        HostService = kind == "Runtime" ? "TechMES.Runtime.Service" : "TechMES.PostgreSql.Agent",
        HostServiceState = "Running",
        HostProcessId = 100,
        Local = new()
        {
            HostKind = kind,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            HealthPeriodSeconds = 10,
            ControllerState = "Monitoring",
            OwnsController = true,
            MonitoringEnabled = true,
            PermissionsOk = true,
            PostgreSqlRole = "Standby",
            Settings = new() { ServiceName = profile.ServiceName, BinDirectory = profile.BinDirectory, DataDirectory = profile.DataDirectory, Port = profile.Port },
            Service = new() { Computer = "server", ServiceName = profile.ServiceName, ServiceState = "Running", DataDirectory = profile.DataDirectory, Port = profile.Port, ManagedByService = true, ServiceProcessId = 200, PostmasterProcessId = 201, ListenerProcessId = 201 }
        }
    };

    /// <summary>Создаёт входной JSON без работы со службами.</summary>
    private string Save(PostgreSqlHostSetupReport report)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(report));
        return path;
    }

    /// <summary>Подставляет минимальный штатный набор инструментов.</summary>
    private string Tools()
    {
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "Prepare-TechMESPostgreSqlController.ps1"), PostgreSqlHostSetupService.ProfileMarker);
        foreach (var name in new[] { "Install-TechMESPostgreSqlHost.ps1", "Set-PostgreSQL18ServiceControl.ps1" })
            File.WriteAllText(Path.Combine(tools, name), "# test tool");
        return tools;
    }

    /// <summary>Создаёт автономный пакет с manifest и контролируемой подменой одного файла.</summary>
    private string Package(bool tampered)
    {
        var package = Path.Combine(_root, "package");
        var files = new[] { "TechMES.PostgreSql.Agent.exe", "TechMES.PostgreSql.Control.dll", "Tools/Install-TechMESPostgreSqlHost.ps1", "Tools/Set-PostgreSQL18ServiceControl.ps1" };
        foreach (var name in files)
        {
            var path = Path.Combine(package, "PostgreSql.Agent", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test bytes");
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("test bytes")));
        File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new { Format = 1, Runtime = "win-x64", SelfContained = true, Components = new[] { new { Key = "postgres-agent", Folder = "PostgreSql.Agent", Service = "TechMES.PostgreSql.Agent", Exe = "TechMES.PostgreSql.Agent.exe", Files = files.Select(name => new { Path = name, Sha256 = hash }).ToArray() } } }));
        if (tampered)
            File.WriteAllText(Path.Combine(package, "PostgreSql.Agent", files[0]), "changed bytes");
        return package;
    }

    /// <summary>Удаляет только изолированный каталог текущего теста.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
