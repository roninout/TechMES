using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Создает и проверяет локальный self-signed сертификат для HTTPS-режима TechMES.Web.
/// Сертификат генерируется как серверный: в Subject Alternative Name попадают localhost,
/// имя компьютера и текущие IPv4-адреса, чтобы Kestrel мог обслуживать планшеты по IP.
/// </summary>
public sealed class HttpsCertificateManager(ServerNetworkService networkService)
{
    private const int CertificateLifetimeYears = 5;

    /// <summary>
    /// Возвращает текущее состояние файлов сертификата без изменения диска.
    /// </summary>
    public HttpsCertificateInfo GetCertificateInfo(ServerOptions options)
    {
        var pfxPath = GetPfxPath(options);
        var publicCertificatePath = GetPublicCertificatePath(options);
        var hasPfx = File.Exists(pfxPath);
        var hasPublicCertificate = File.Exists(publicCertificatePath);

        string thumbprint = "";
        DateTimeOffset? notAfter = null;

        if (hasPfx)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(
                    pfxPath,
                    options.CertificatePassword,
                    X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057

                thumbprint = certificate.Thumbprint;
                notAfter = certificate.NotAfter;
            }
            catch
            {
                thumbprint = "Cannot read PFX";
            }
        }

        return new HttpsCertificateInfo
        {
            HasPfx = hasPfx,
            HasPublicCertificate = hasPublicCertificate,
            PfxPath = pfxPath,
            PublicCertificatePath = publicCertificatePath,
            Thumbprint = thumbprint,
            NotAfter = notAfter
        };
    }

    /// <summary>
    /// Создает новый PFX/CER комплект. Старые файлы перезаписываются, потому что IP-адреса могли измениться.
    /// </summary>
    public HttpsCertificateInfo CreateOrReplaceCertificate(ServerOptions options)
    {
        Directory.CreateDirectory(options.CertificateDirectory);

        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={options.CertificateSubject}");
        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        var serverAuthentication = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(serverAuthentication, critical: false));

        request.CertificateExtensions.Add(BuildSubjectAlternativeNames());

        var notBefore = DateTimeOffset.Now.AddDays(-1);
        var notAfter = DateTimeOffset.Now.AddYears(CertificateLifetimeYears);

        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var pfxPath = GetPfxPath(options);
        var publicCertificatePath = GetPublicCertificatePath(options);

        File.WriteAllBytes(
            pfxPath,
            certificate.Export(X509ContentType.Pfx, options.CertificatePassword));

        File.WriteAllBytes(
            publicCertificatePath,
            certificate.Export(X509ContentType.Cert));

        return GetCertificateInfo(options);
    }

    /// <summary>
    /// Формирует полный путь к PFX-файлу.
    /// </summary>
    public static string GetPfxPath(ServerOptions options) =>
        Path.Combine(options.CertificateDirectory, options.CertificateFileName);

    /// <summary>
    /// Формирует полный путь к CER-файлу.
    /// </summary>
    public static string GetPublicCertificatePath(ServerOptions options) =>
        Path.Combine(options.CertificateDirectory, options.PublicCertificateFileName);

    /// <summary>
    /// Собирает SAN-расширение сертификата: браузер сверяет именно эти DNS/IP значения с URL.
    /// </summary>
    private X509Extension BuildSubjectAlternativeNames()
    {
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddDnsName("localhost");
        builder.AddDnsName(Environment.MachineName);

        try
        {
            builder.AddDnsName(Dns.GetHostName());
        }
        catch
        {
            // Имя хоста не критично: сертификат все равно будет содержать localhost и IP-адреса.
        }

        foreach (var (_, address) in networkService.GetLocalIPv4Addresses())
            builder.AddIpAddress(address);

        builder.AddIpAddress(IPAddress.Loopback);
        return builder.Build();
    }
}
