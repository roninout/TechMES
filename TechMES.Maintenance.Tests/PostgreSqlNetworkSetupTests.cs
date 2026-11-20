using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

public sealed class PostgreSqlNetworkSetupTests : IDisposable
{
    private const string FirstAddress = "192.0.2.11";
    private const string SecondAddress = "192.0.2.12";
    private const string SystemIdentifier = "7703468038317098096";
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void CopiedCertificateMustMatchIndependentPinAndSelectedNode()
    {
        var (path, pin) = CreateCertificate(FirstAddress);

        Assert.Equal(pin, PostgreSqlNetworkSetupService.ReadCertificate(path, pin.ToLowerInvariant(), FirstAddress).Sha256);
        Assert.Throws<InvalidDataException>(() => PostgreSqlNetworkSetupService.ReadCertificate(path, new string('0', 64), FirstAddress));
        Assert.Throws<InvalidDataException>(() => PostgreSqlNetworkSetupService.ReadCertificate(path, pin, SecondAddress));
    }

    [Fact]
    public void ConnectBundleCarriesTheOtherNodePublicCertificateAndPin()
    {
        var (firstPath, firstPin) = CreateCertificate(FirstAddress);
        var (secondPath, secondPin) = CreateCertificate(SecondAddress);
        var first = PostgreSqlNetworkSetupService.ReadCertificate(firstPath, firstPin, FirstAddress);
        var second = PostgreSqlNetworkSetupService.ReadCertificate(secondPath, secondPin, SecondAddress);
        var service = new PostgreSqlNetworkSetupService(Path.Combine(AppContext.BaseDirectory, "Tools"));

        var root = service.Export(_directory, PostgreSqlNetworkSetupStage.ConnectNodes, Node(FirstAddress), Node(SecondAddress),
            SystemIdentifier, 5115, "", first, second);

        AssertBundle(FirstAddress, SecondAddress, second);
        AssertBundle(SecondAddress, FirstAddress, first);

        void AssertBundle(string address, string peerAddress, PostgreSqlPublicCertificate peer)
        {
            var folder = Path.Combine(root, address);
            Assert.Equal(peer.Bytes, File.ReadAllBytes(Path.Combine(folder, "peer.cer")));
            var payload = Regex.Match(File.ReadAllText(Path.Combine(folder, "Setup-Network.ps1")), "EmbeddedNetworkSetup = '([^']+)'").Groups[1].Value;
            using var options = JsonDocument.Parse(Convert.FromBase64String(payload));
            Assert.Equal(address, options.RootElement.GetProperty("NodeAddress").GetString());
            Assert.Equal(peerAddress, options.RootElement.GetProperty("PeerAddress").GetString());
            Assert.Equal(peer.Sha256, options.RootElement.GetProperty("PeerSha256").GetString());
            Assert.Equal(SystemIdentifier, options.RootElement.GetProperty("SystemIdentifier").GetString());
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (string Path, string Pin) CreateCertificate(string address)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=TechMES PostgreSQL " + address, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(_directory, address + ".cer");
        File.WriteAllBytes(path, certificate.RawData);
        return (path, Convert.ToHexString(SHA256.HashData(certificate.RawData)));
    }

    private static PostgreSqlNetworkSetupNode Node(string address) => new(new PostgreSqlSetupProfile
    {
        ComputerName = address,
        BinDirectory = @"D:\PostgreSQL\18\bin",
        DataDirectory = @"D:\PostgreSQL\18\data"
    }, address);
}
