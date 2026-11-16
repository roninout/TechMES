using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Host;
using TechMES.Host.Control;

namespace TechMES.Host.Tests;

/// <summary>Проверяет реальное mTLS-соединение и правила закреплённых сертификатов.</summary>
public sealed class HostControlTlsTests
{
    [Theory]
    [InlineData("peer", true)]
    [InlineData("own", true)]
    [InlineData("unknown", false)]
    [InlineData("none", false)]
    [InlineData("wrong_server_pin", false)]
    public async Task MutualTlsRequiresTrustedClientAndPinnedServer(string mode, bool accepted)
    {
        using var server = Certificate("server");
        using var peer = Certificate("peer");
        using var unknown = Certificate("unknown");

        var settings = new HostControlSettings
        {
            Enabled = true,
            CertificateSha256 = Pin(server),
            PeerCertificateSha256 = Pin(peer)
        };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(https =>
        {
            https.ServerCertificate = server;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (client, _, _) => HostNodeCertificates.AcceptClient(client, settings);
        })));

        await using var app = builder.Build();

        app.MapGet("/", () => "Host control");

        await app.StartAsync();

        try
        {
            var address = app.Urls.Single();

            // Каждый сценарий сначала подтверждает рабочий TLS с доверенным клиентом.
            // Иначе отрицательный тест мог бы пройти из-за поломки самого TLS-стенда.
            using (var probe = CreateClient(peer, Pin(server)))
                Assert.Equal("Host control", await probe.GetStringAsync(address));

            var clientCertificate = mode switch
            {
                "none" => null,
                "own" => server,
                "unknown" => unknown,
                _ => peer
            };

            var serverPin = mode == "wrong_server_pin" ? Pin(unknown) : Pin(server);

            using var client = CreateClient(clientCertificate, serverPin);

            if (accepted)
                Assert.Equal("Host control", await client.GetStringAsync(address));
            else
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(address));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void CorrectPinDoesNotOverrideServerNameMismatch()
    {
        using var certificate = Certificate("server");

        Assert.False(HostNodeCertificates.AcceptServer(certificate, Pin(certificate), SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void PinnedCertificateStillRequiresClientEku()
    {
        using var certificate = Certificate("server-only", includeClientEku: false);

        Assert.False(HostNodeCertificates.Matches(certificate, Pin(certificate), HostNodeCertificates.ClientEku));
    }

    [Fact]
    public void PinnedExpiredCertificateIsRejected()
    {
        using var certificate = Certificate("expired", expired: true);

        Assert.False(HostNodeCertificates.Matches(certificate, Pin(certificate), HostNodeCertificates.ServerEku));
    }

    /// <summary>Создаёт отдельный клиент: pin сервера обязателен, сертификат клиента задаётся сценарием.</summary>
    private static HttpClient CreateClient(X509Certificate2? certificate, string serverPin)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        };

        if (certificate is not null)
            handler.ClientCertificates.Add(certificate);

        handler.ServerCertificateCustomValidationCallback = (_, server, _, errors) => HostNodeCertificates.AcceptServer(server, serverPin, errors);

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>Возвращает публичный SHA256 сертификата.</summary>
    private static string Pin(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>Создаёт тестовый сертификат с приватным ключом, доступным TLS-провайдеру текущей ОС.</summary>
    private static X509Certificate2 Certificate(string name, bool includeClientEku = true, bool expired = false)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var usages = new OidCollection
        {
            new(HostNodeCertificates.ServerEku)
        };

        if (includeClientEku)
            usages.Add(new(HostNodeCertificates.ClientEku));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);

        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1));

        // Schannel на Windows требует обычного импорта ключа.
        // DefaultKeySet не добавляет сертификат в Trusted Root и не требует прав администратора.
        var keyStorage = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, keyStorage);
    }
}