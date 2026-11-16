using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TechMES.Contracts.Host;

namespace TechMES.Host.Control;

/// <summary>Общие правила сертификатов Host; этот файл также компилируется в Maintenance.</summary>
public static class HostNodeCertificates
{
    public const string ServerEku = "1.3.6.1.5.5.7.3.1";
    public const string ClientEku = "1.3.6.1.5.5.7.3.2";

    /// <summary>Проверяет точный SHA256, срок действия и явный EKU.</summary>
    public static bool Matches(X509Certificate2? certificate, string pin, string eku) => certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(pin, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() && certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == eku));

    /// <summary>Принимает только закреплённый сертификат своего либо второго узла.</summary>
    public static bool AcceptClient(X509Certificate2? certificate, HostControlSettings settings) => Matches(certificate, settings.CertificateSha256, ClientEku) || Matches(certificate, settings.PeerCertificateSha256, ClientEku);

    /// <summary>Pin заменяет доверие self-signed цепочке; проверка имени сервера сохраняется.</summary>
    public static bool AcceptServer(X509Certificate2? certificate, string pin, SslPolicyErrors errors) => (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 && Matches(certificate, pin, ServerEku);

    /// <summary>Загружает приватный ключ локального узла и проверяет IP в SAN сертификата.</summary>
    public static X509Certificate2 LoadLocal(string pin, string nodeAddress)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Host uses LocalMachine/My on Windows.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificates = store.Certificates.Cast<X509Certificate2>().Where(certificate => certificate.HasPrivateKey && Matches(certificate, pin, ServerEku) && Matches(certificate, pin, ClientEku)).ToArray();
        if (certificates.Length != 1) throw new InvalidOperationException("В LocalMachine/My не найден единственный действующий сертификат Host с приватным ключом и двумя EKU.");
        var selected = certificates[0];
        if (!selected.MatchesHostname(nodeAddress, allowWildcards: false, allowCommonName: false)) throw new InvalidOperationException("Собственный IP не входит в SAN сертификата Host.");
        return selected;
    }

    /// <summary>Создаёт mTLS-клиент без redirects, cookies и системного proxy.</summary>
    public static HttpClient CreateClient(HostControlSettings settings, string nodeAddress)
    {
        var certificate = LoadLocal(settings.CertificateSha256, nodeAddress);
        var handler = new CertificateHandler(certificate) { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        var peerPin = settings.PeerCertificateSha256;
        handler.ServerCertificateCustomValidationCallback = (_, server, _, errors) => AcceptServer(server, peerPin, errors);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Закрывает загруженный приватный ключ вместе с HTTP transport.</summary>
    private sealed class CertificateHandler : HttpClientHandler
    {
        private readonly X509Certificate2 _certificate;
        public CertificateHandler(X509Certificate2 certificate) { _certificate = certificate; ClientCertificates.Add(certificate); }
        protected override void Dispose(bool disposing) { try { base.Dispose(disposing); } finally { if (disposing) _certificate.Dispose(); } }
    }
}
