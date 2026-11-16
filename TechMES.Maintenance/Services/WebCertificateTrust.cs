using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Проверяет публичный WEB CER и устанавливает доверие
/// только к выбранной копии.
/// </summary>
public sealed class WebCertificateTrust
{
    private readonly byte[] _rawData;

    public string SourcePath { get; }

    private WebCertificateTrust(string sourcePath, byte[] rawData)
    {
        SourcePath = sourcePath;
        _rawData = rawData;
    }

    /// <summary>
    /// Загружает одиночный публичный сертификат.
    /// PFX и закрытые ключи не принимаются.
    /// </summary>
    public static WebCertificateTrust Load(string path)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(path));
        return new WebCertificateTrust(Path.GetFullPath(path), certificate.RawData);
    }

    /// <summary>
    /// Проверяет ограничения текущей схемы
    /// с отдельными self-signed WEB-сертификатами.
    /// </summary>
    public void ValidateForImport()
    {
        using var certificate = X509CertificateLoader.LoadCertificate(_rawData);
        var now = DateTime.UtcNow;

        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("Сертификат ещё не действует или срок его действия истёк.");

        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            throw new InvalidOperationException("Для сертификата, выданного центром сертификации, настройте доверие к его CA. Эта кнопка предназначена для self-signed WEB CER.");

        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
            throw new InvalidOperationException("Выбран сертификат CA. Здесь ожидается публичный WEB-сертификат узла.");

        var serverAuth = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"));

        if (!serverAuth)
            throw new InvalidOperationException("В сертификате отсутствует назначение Server Authentication.");

        if (!certificate.Extensions.Cast<X509Extension>().Any(extension => extension.Oid?.Value == "2.5.29.17"))
            throw new InvalidOperationException("В сертификате отсутствует Subject Alternative Name (SAN).");
    }

    /// <summary>
    /// Показывает данные выбранной копии и наличие
    /// именно её в хранилище компьютера.
    /// </summary>
    public string Describe()
    {
        using var certificate = X509CertificateLoader.LoadCertificate(_rawData);
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);

        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        var installed = false;

        foreach (var existing in store.Certificates)
        {
            using (existing)
                installed |= existing.RawData.AsSpan().SequenceEqual(_rawData);
        }

        var san = certificate.Extensions.Cast<X509Extension>()
            .FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17")?.Format(true) ?? "Отсутствует";

        var eligibility = "Допускается импорт после сверки SHA256 и имени в SAN.";

        try
        {
            ValidateForImport();
        }
        catch (InvalidOperationException ex)
        {
            eligibility = "Импорт запрещён: " + ex.Message;
        }

        return $"Файл: {SourcePath}\nSubject: {certificate.Subject}\nIssuer: {certificate.Issuer}\n" +
            $"Действует: {certificate.NotBefore:yyyy-MM-dd HH:mm:ss} — {certificate.NotAfter:yyyy-MM-dd HH:mm:ss}\n" +
            $"Thumbprint (SHA1): {certificate.Thumbprint}\nSHA256 сертификата (DER): {Convert.ToHexString(SHA256.HashData(_rawData))}\n" +
            $"SAN:\n{san}\nLocalMachine\\Root: {(installed ? "установлен" : "не установлен")}\n{eligibility}";
    }

    /// <summary>
    /// Добавляет просмотренную копию в доверенные
    /// для всех пользователей этого ПК.
    /// </summary>
    public void Install()
    {
        ValidateForImport();

        using var certificate = X509CertificateLoader.LoadCertificate(_rawData);
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);

        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }
}