using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

internal sealed record PostgreSqlNetworkSetupNode(PostgreSqlSetupProfile Profile, string Address);
internal sealed record PostgreSqlPublicCertificate(byte[] Bytes, string Sha256);

internal enum PostgreSqlNetworkSetupStage { PrepareNodes, ConnectNodes, PrepareMaintenance, TrustMaintenance }

internal sealed class PostgreSqlNetworkSetupService(string toolsDirectory)
{
    private const string Marker = "$script:EmbeddedNetworkSetup = ''";

    internal static void ValidateAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || ip.ToString() != address || ip.GetAddressBytes()[0] is not (> 0 and < 224) || IPAddress.IsLoopback(ip))
            throw new InvalidDataException("Введите IPv4 узла или компьютера Maintenance: " + address);
    }

    internal static PostgreSqlPublicCertificate ReadCertificate(string path, string expectedSha256, string? nodeAddress)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Выберите файл public.cer для " + (nodeAddress ?? "Maintenance") + ".");
        if (!Regex.IsMatch(expectedSha256, "^[0-9A-Fa-f]{64}$"))
            throw new InvalidDataException("Введите SHA-256, показанный PowerShell на компьютере, где создан сертификат.");

        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > 65536)
            throw new InvalidDataException("Некорректный размер файла сертификата.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (X509Certificate2.GetCertContentType(bytes) != X509ContentType.Cert)
            throw new InvalidDataException("Выберите открытый сертификат .cer.");

        using var cert = X509CertificateLoader.LoadCertificate(bytes);
        var pin = Convert.ToHexString(SHA256.HashData(cert.RawData));
        if (!string.Equals(pin, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 файла отличается от отпечатка, показанного на сервере.");

        if (nodeAddress is not null ? cert.Subject != "CN=TechMES PostgreSQL " + nodeAddress
            : !cert.Subject.StartsWith("CN=TechMES PostgreSQL Maintenance ", StringComparison.Ordinal))
            throw new InvalidDataException("Сертификат выпущен для другого узла или другой роли.");

        var now = DateTime.UtcNow;
        if (cert.NotBefore.ToUniversalTime() > now || cert.NotAfter.ToUniversalTime() <= now)
            throw new InvalidDataException("Проверьте срок действия сертификата и время на компьютерах.");

        return new(cert.RawData, pin);
    }

    internal string Export(string destination, PostgreSqlNetworkSetupStage stage, PostgreSqlNetworkSetupNode first, PostgreSqlNetworkSetupNode second,
        string systemIdentifier, int port, string maintenanceAddress, PostgreSqlPublicCertificate? firstCert = null,
        PostgreSqlPublicCertificate? secondCert = null, PostgreSqlPublicCertificate? maintenanceCert = null)
    {
        foreach (var node in new[] { first, second })
        {
            node.Profile.ValidateHost();
            ValidateAddress(node.Address);
            if (node.Profile.Port == port)
                throw new InvalidDataException("Порт mTLS должен отличаться от порта PostgreSQL.");
        }

        if (first.Address == second.Address || !Regex.IsMatch(systemIdentifier, "^[0-9]{1,20}$") || port is < 1024 or > 65535)
            throw new InvalidDataException("Укажите два разных адреса, идентификатор кластера и порт mTLS от 1024 до 65535.");

        if (stage is PostgreSqlNetworkSetupStage.ConnectNodes or PostgreSqlNetworkSetupStage.PrepareMaintenance)
        {
            if (firstCert is null || secondCert is null || firstCert.Sha256 == secondCert.Sha256)
                throw new InvalidDataException("Нужны два разных проверенных сертификата узлов.");
        }

        if (stage is PostgreSqlNetworkSetupStage.PrepareMaintenance or PostgreSqlNetworkSetupStage.TrustMaintenance)
            ValidateAddress(maintenanceAddress);
        if (stage == PostgreSqlNetworkSetupStage.TrustMaintenance && maintenanceCert is null)
            throw new InvalidDataException("Подготовьте клиентский сертификат Maintenance.");

        var template = File.ReadAllText(Path.Combine(toolsDirectory, "Setup-TechMESPostgreSqlNetworkBundle.ps1"));
        if (template.Split(Marker).Length != 2)
            throw new InvalidDataException("Не найдена точка подстановки профиля в скрипте настройки.");

        var root = Path.Combine(destination, $"PostgreSQL-{stage}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            if (stage == PostgreSqlNetworkSetupStage.PrepareMaintenance)
                WriteBundle(root, first, second, firstCert, secondCert);
            else
            {
                WriteBundle(Path.Combine(root, first.Address), first, second, firstCert, secondCert);
                WriteBundle(Path.Combine(root, second.Address), second, first, secondCert, firstCert);
            }

            return root;
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }

        void WriteBundle(string folder, PostgreSqlNetworkSetupNode local, PostgreSqlNetworkSetupNode peer, PostgreSqlPublicCertificate? localCert, PostgreSqlPublicCertificate? peerCert)
        {
            var tools = Path.Combine(folder, "Tools");
            Directory.CreateDirectory(tools);
            foreach (var tool in new[] { "Configure-TechMESPostgreSqlNetwork.ps1", "Configure-TechMESPostgreSqlMaintenance.ps1", "Invoke-TechMESPostgreSqlCommand.ps1" })
                File.Copy(Path.Combine(toolsDirectory, tool), Path.Combine(tools, tool));

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Mode = stage.ToString(), NodeAddress = local.Address, PeerAddress = peer.Address, SystemIdentifier = systemIdentifier,
                Port = port, MaintenanceAddress = maintenanceAddress, local.Profile.ComputerName, local.Profile.ServiceName, local.Profile.HostMode,
                NodeSha256 = localCert?.Sha256, PeerSha256 = peerCert?.Sha256, MaintenanceSha256 = maintenanceCert?.Sha256
            });
            var script = template.Replace(Marker, "$script:EmbeddedNetworkSetup = '" + Convert.ToBase64String(payload) + "'", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(folder, "Setup-Network.ps1"), script, new UTF8Encoding(true));

            if (stage == PostgreSqlNetworkSetupStage.ConnectNodes)
                File.WriteAllBytes(Path.Combine(folder, "peer.cer"), peerCert!.Bytes);
            if (stage == PostgreSqlNetworkSetupStage.TrustMaintenance)
                File.WriteAllBytes(Path.Combine(folder, "maintenance.cer"), maintenanceCert!.Bytes);
        }
    }
}
