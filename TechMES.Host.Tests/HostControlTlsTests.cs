using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using TechMES.Contracts.Host;
using TechMES.Host.Control;

namespace TechMES.Host.Tests;

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
        var settings = new HostControlSettings { Enabled = true, CertificateSha256 = Pin(server), PeerCertificateSha256 = Pin(peer) };
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
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        if (mode != "none") handler.ClientCertificates.Add(mode == "own" ? server : mode == "unknown" ? unknown : peer);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => HostNodeCertificates.AcceptServer(certificate, mode == "wrong_server_pin" ? Pin(unknown) : Pin(server), errors);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        if (accepted) Assert.Equal("Host control", await client.GetStringAsync(app.Urls.Single()));
        else await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(app.Urls.Single()));
        await app.StopAsync();
    }

    [Fact]
    public void CorrectPinDoesNotOverrideServerNameMismatch()
    {
        using var certificate = Certificate("server");
        Assert.False(HostNodeCertificates.AcceptServer(certificate, Pin(certificate), System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch));
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

    private static string Pin(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    private static X509Certificate2 Certificate(string name, bool includeClientEku = true, bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(includeClientEku ? new OidCollection { new(HostNodeCertificates.ServerEku), new(HostNodeCertificates.ClientEku) } : new OidCollection { new(HostNodeCertificates.ServerEku) }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
