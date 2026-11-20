using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

public sealed class PostgreSqlInitialStandbyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly PostgreSqlInitialStandbyService _service = new(Path.Combine(AppContext.BaseDirectory, "Tools"));

    [Fact]
    public void BundleUsesTheTargetProfileAndTheSourceClusterIdentifier()
    {
        const string sourceAddress = "192.0.2.12";
        const string targetAddress = "192.0.2.11";
        const string cluster = "7703468038317098096";
        var target = Profile(targetAddress);
        target.DataDirectory = @"E:\PostgreSQL 18\data";

        var directory = _service.Export(_directory, Profile(sourceAddress), sourceAddress, target, targetAddress, cluster);
        var launcher = File.ReadAllText(Path.Combine(directory, "Setup-Standby.ps1"));
        var payload = Regex.Match(launcher, "EmbeddedInitialStandby = '([^']+)'").Groups[1].Value;
        using var options = JsonDocument.Parse(Convert.FromBase64String(payload));
        Assert.Equal(targetAddress, options.RootElement.GetProperty("TargetAddress").GetString());
        Assert.Equal(sourceAddress, options.RootElement.GetProperty("SourceAddress").GetString());
        Assert.Equal(target.DataDirectory, options.RootElement.GetProperty("DataDirectory").GetString());
        Assert.Equal(cluster, options.RootElement.GetProperty("SystemIdentifier").GetString());
        Assert.True(File.Exists(Path.Combine(directory, "Tools", "Switch-PostgreSQL18.ps1")));
        Assert.True(File.Exists(Path.Combine(directory, "Tools", "Set-PostgreSQL18ServiceControl.ps1")));
    }

    [Fact]
    public void ExportRejectsEqualAddressesAndMismatchedPorts()
    {
        var source = Profile("192.0.2.12");
        var target = Profile("192.0.2.11");
        Assert.Throws<InvalidDataException>(() => _service.Export(_directory, source, source.ComputerName, target, source.ComputerName, "123"));

        target.Port++;
        Assert.Throws<InvalidDataException>(() => _service.Export(_directory, source, source.ComputerName, target, target.ComputerName, "123"));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PostgreSqlSetupProfile Profile(string address) => new()
    {
        ComputerName = address,
        BinDirectory = @"D:\PostgreSQL\18\bin",
        DataDirectory = @"D:\PostgreSQL\18\data"
    };
}
