using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

public sealed class WebCertificateTrustTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("expired", false)]
    [InlineData("future", false)]
    [InlineData("ca", false)]
    [InlineData("client", false)]
    [InlineData("without-san", false)]
    public void ImportEligibilityRejectsWrongCertificatePurposeAndValidity(string variant, bool allowed)
    {
        using var key = RSA.Create(2048);

        var request = CreateRequest(key, variant);
        var now = DateTimeOffset.UtcNow;
        var start = variant == "future" ? now.AddDays(1) : now.AddDays(-2);
        var end = variant == "expired" ? now.AddDays(-1) : now.AddDays(2);

        using var certificate = request.CreateSelfSigned(start, end);

        WithFile(certificate.Export(X509ContentType.Cert), path =>
        {
            var selected = WebCertificateTrust.Load(path);

            if (allowed)
                selected.ValidateForImport();
            else
                Assert.Throws<InvalidOperationException>(selected.ValidateForImport);
        });
    }

    [Fact]
    public void PfxWithPrivateKeyIsNotAcceptedAsPublicCer()
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateRequest(key, "valid").CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        WithFile(certificate.Export(X509ContentType.Pfx, ""), path =>
            Assert.ThrowsAny<CryptographicException>(() => WebCertificateTrust.Load(path)));
    }

    [Fact]
    public void ReplacingFileAfterSelectionDoesNotReplaceReviewedCertificate()
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateRequest(key, "valid").CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        WithFile(certificate.Export(X509ContentType.Cert), path =>
        {
            var selected = WebCertificateTrust.Load(path);

            File.WriteAllText(path, "replaced");

            selected.ValidateForImport();

            Assert.ThrowsAny<CryptographicException>(() => WebCertificateTrust.Load(path));
        });
    }

    [Fact]
    public void CaIssuedLeafCannotBeImportedAsSelfSignedRoot()
    {
        using var issuerKey = RSA.Create(2048);

        var issuerRequest = new CertificateRequest("CN=Test CA", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        using var key = RSA.Create(2048);
        using var certificate = CreateRequest(key, "valid").Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));

        WithFile(certificate.Export(X509ContentType.Cert), path =>
            Assert.Throws<InvalidOperationException>(WebCertificateTrust.Load(path).ValidateForImport));
    }

    private static CertificateRequest CreateRequest(RSA key, string variant)
    {
        var request = new CertificateRequest("CN=Test WEB", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(variant == "ca", false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(variant == "client" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, false));

        if (variant != "without-san")
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("web.example.test");
            request.CertificateExtensions.Add(san.Build());
        }

        return request;
    }

    private static void WithFile(byte[] data, Action<string> action)
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(path, data);
            action(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}