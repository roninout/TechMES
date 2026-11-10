using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

public sealed record PostgreSqlNetworkOptions
{
    public const string SectionName = "PostgreSqlNetwork";

    public bool Enabled { get; init; }
    public string NodeAddress { get; init; } = "";
    public string PeerAddress { get; init; } = "";
    public int Port { get; init; } = 5115;
    public string CertificateSha256 { get; init; } = "";
    public string PeerCertificateSha256 { get; init; } = "";

    // Проверяет адреса пары, отдельный HTTPS-порт и привязку к кластеру.
    public void Validate(PostgreSqlControlOptions control)
    {
        if (!Enabled || NodeAddress is not ("192.168.1.131" or "192.168.1.132") || PeerAddress is not ("192.168.1.131" or "192.168.1.132") || NodeAddress == PeerAddress || Port is < 1024 or > 65535 || Port == control.Port || !Regex.IsMatch(CertificateSha256, "^[0-9A-Fa-f]{64}$") || !Regex.IsMatch(PeerCertificateSha256, "^[0-9A-Fa-f]{64}$") || CertificateSha256.Equals(PeerCertificateSha256, StringComparison.OrdinalIgnoreCase) || !control.CommandsEnabled || !Regex.IsMatch(control.ExpectedSystemIdentifier, "^[0-9]{1,20}$"))
            throw new InvalidOperationException("Invalid PostgreSQL network settings or disabled local commands.");
    }
}

public sealed record PostgreSqlSwitchPlan(string Id, string From, string To, string SystemIdentifier, DateTimeOffset CreatedAtUtc)
{
    // Привязывает план к одной настроенной паре и одному кластеру.
    public void Validate(PostgreSqlNetworkOptions network, PostgreSqlControlOptions control)
    {
        if (!Regex.IsMatch(Id, "^[0-9a-f]{32}$") || From == To || !new[] { network.NodeAddress, network.PeerAddress }.Contains(From) || !new[] { network.NodeAddress, network.PeerAddress }.Contains(To) || SystemIdentifier != control.ExpectedSystemIdentifier)
            throw new InvalidOperationException("The switch plan does not match this node and cluster.");
    }

    // Одна фаза одного переключения всегда получает один OperationId.
    public string OperationId(string mode)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Id}|{From}|{To}|{SystemIdentifier}|{mode}")))[..32].ToLowerInvariant();
    }
}

public sealed record PostgreSqlPhaseRequest(PostgreSqlSwitchPlan Plan, JsonElement? Fence);

public sealed record PostgreSqlRemoteOperation(string Id, string State, string Message, JsonElement? Result, JsonElement? Fence);

public sealed record PostgreSqlRemoteStatus(string NodeAddress, PostgreSqlLocalHealthResponse Local);

public static class PostgreSqlNodeCertificates
{
    // Требует точный pin, срок действия и явно указанный EKU.
    public static bool Matches(X509Certificate2? certificate, string pin, string eku)
    {
        if (certificate is null || !certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(pin, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
            return false;

        return certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == eku));
    }

    // Находит локальный сертификат с приватным ключом в машинном хранилище.
    public static X509Certificate2 LoadLocal(PostgreSqlNetworkOptions network)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Use the LocalMachine certificate store on Windows.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

        store.Open(OpenFlags.ReadOnly);

        return store.Certificates.Cast<X509Certificate2>().Single(certificate => certificate.HasPrivateKey && Matches(certificate, network.CertificateSha256, "1.3.6.1.5.5.7.3.1") && Matches(certificate, network.CertificateSha256, "1.3.6.1.5.5.7.3.2"));
    }

    // Проверяет IP/DNS имя серверного сертификата даже при точном pin.
    public static bool AcceptServer(X509Certificate2? certificate, string pin, SslPolicyErrors errors)
    {
        return (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 && Matches(certificate, pin, "1.3.6.1.5.5.7.3.1");
    }
}