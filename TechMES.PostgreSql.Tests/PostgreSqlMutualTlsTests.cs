using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TechMES.PostgreSql.Control;

namespace TechMES.PostgreSql.Tests;

public sealed class PostgreSqlMutualTlsTests
{
    // Listener требует доверенный клиентский сертификат и правильный pin сервера.
    [Fact]
    public async Task HttpsRequiresBothTrustedClientAndPinnedServer()
    {
        using var serverCertificate = Certificate("server");
        using var clientCertificate = Certificate("client");

        var serverPin = serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var clientPin = clientCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => PostgreSqlNodeCertificates.Matches(certificate, clientPin, "1.3.6.1.5.5.7.3.2");
        })));

        await using var app = builder.Build();

        app.MapGet("/v1/status", () => new PostgreSqlRemoteStatus("127.0.0.1", new() { ControllerState = "Monitoring" }));

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var options = new PostgreSqlNetworkOptions { NodeAddress = "127.0.0.1", PeerAddress = "127.0.0.2", Port = new Uri(address).Port, CertificateSha256 = serverPin, PeerCertificateSha256 = clientPin };

        using (var valid = new PostgreSqlSwitchRemote(options, clientCertificate))
            Assert.Equal("Monitoring", (await valid.StatusAsync("127.0.0.1", CancellationToken.None)).Local.ControllerState);

        using (var wrongPin = new PostgreSqlSwitchRemote(options with { CertificateSha256 = new string('0', 64) }, clientCertificate))
            await Assert.ThrowsAsync<HttpRequestException>(() => wrongPin.StatusAsync("127.0.0.1", CancellationToken.None));

        using (var untrustedClient = new PostgreSqlSwitchRemote(options, serverCertificate))
            await Assert.ThrowsAsync<HttpRequestException>(() => untrustedClient.StatusAsync("127.0.0.1", CancellationToken.None));

        using var anonymous = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => PostgreSqlNodeCertificates.AcceptServer(certificate, serverPin, errors) });

        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.GetAsync(address + "/v1/status"));

        await app.StopAsync();
    }

    // Создаёт временные сертификаты с обоими EKU и правильным IP SAN.
    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));

        var names = new SubjectAlternativeNameBuilder();

        names.AddIpAddress(IPAddress.Loopback);

        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}