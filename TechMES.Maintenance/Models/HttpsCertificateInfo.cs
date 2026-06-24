namespace TechMES.Maintenance.Models;

/// <summary>
/// Состояние локального HTTPS-сертификата, который Maintenance готовит для Kestrel.
/// PFX используется WEB-сервисом, а CER можно установить на клиентские устройства как доверенный.
/// </summary>
public sealed class HttpsCertificateInfo
{
    /// <summary>
    /// Найден ли PFX-файл с закрытым ключом.
    /// </summary>
    public bool HasPfx { get; init; }

    /// <summary>
    /// Найден ли публичный CER-файл.
    /// </summary>
    public bool HasPublicCertificate { get; init; }

    /// <summary>
    /// Полный путь к PFX-файлу.
    /// </summary>
    public string PfxPath { get; init; } = "";

    /// <summary>
    /// Полный путь к публичному CER-файлу.
    /// </summary>
    public string PublicCertificatePath { get; init; } = "";

    /// <summary>
    /// Thumbprint сертификата, если PFX удалось прочитать.
    /// </summary>
    public string Thumbprint { get; init; } = "";

    /// <summary>
    /// Дата окончания действия сертификата.
    /// </summary>
    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>
    /// Короткая строка для отображения в UI.
    /// </summary>
    public string Status
    {
        get
        {
            if (!HasPfx)
                return "Missing PFX";

            if (!HasPublicCertificate)
                return "PFX only";

            return NotAfter is null
                ? "Ready"
                : $"Ready, valid to {NotAfter:yyyy-MM-dd}";
        }
    }
}
