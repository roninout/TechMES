using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

internal sealed class PostgreSqlInitialStandbyService(string toolsDirectory)
{
    private const string Marker = "$script:EmbeddedInitialStandby = ''";

    internal string Export(string destination, PostgreSqlSetupProfile source, string sourceAddress,
        PostgreSqlSetupProfile target, string targetAddress, string systemIdentifier)
    {
        source.Validate();
        target.Validate();
        PostgreSqlNetworkSetupService.ValidateAddress(sourceAddress);
        PostgreSqlNetworkSetupService.ValidateAddress(targetAddress);
        if (sourceAddress == targetAddress || source.Port != target.Port || !Regex.IsMatch(systemIdentifier, @"\A[0-9]{1,20}\z"))
            throw new InvalidDataException("Укажите два разных адреса, идентификатор Primary и одинаковый порт PostgreSQL в обоих профилях.");

        var template = File.ReadAllText(Path.Combine(toolsDirectory, "Setup-TechMESPostgreSqlInitialStandby.ps1"));
        if (template.Split(Marker).Length != 2)
            throw new InvalidDataException("Скрипт создания Standby не содержит места для профиля.");

        var directory = Path.Combine(destination, $"PostgreSQL-InitialStandby-{targetAddress}-{Guid.NewGuid():N}");
        var tools = Path.Combine(directory, "Tools");
        Directory.CreateDirectory(tools);
        try
        {
            foreach (var file in new[] { "Switch-PostgreSQL18.ps1", "Set-PostgreSQL18ServiceControl.ps1" })
                File.Copy(Path.Combine(toolsDirectory, file), Path.Combine(tools, file));

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SourceAddress = sourceAddress,
                TargetAddress = targetAddress,
                SystemIdentifier = systemIdentifier,
                target.ComputerName,
                target.ServiceName,
                target.BinDirectory,
                target.DataDirectory,
                target.Port
            });
            var script = template.Replace(Marker, "$script:EmbeddedInitialStandby = '" + Convert.ToBase64String(payload) + "'", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, "Setup-Standby.ps1"), script, new UTF8Encoding(true));
            return directory;
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }
}
