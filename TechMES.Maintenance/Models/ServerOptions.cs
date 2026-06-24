namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки сетевого доступа к серверной установке TechMES.
/// Эти параметры использует Maintenance: они помогают проверить порты,
/// показать оператору URL для планшета и подготовить firewall/HTTPS.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// HTTP TCP-порт, на котором TechMES.Web слушает внешние подключения через Kestrel.
    /// </summary>
    public int WebPort { get; set; } = 5163;

    /// <summary>
    /// HTTPS TCP-порт WEB. Его открываем отдельно, чтобы HTTP оставался рабочим fallback-каналом.
    /// </summary>
    public int HttpsPort { get; set; } = 7163;

    /// <summary>
    /// TCP-порт Runtime.Service. По текущей архитектуре он слушает только localhost.
    /// </summary>
    public int RuntimePort { get; set; } = 5101;

    /// <summary>
    /// Имя входящего правила Windows Firewall для HTTP-порта WEB.
    /// </summary>
    public string FirewallRuleName { get; set; } = "TechMES WEB 5163";

    /// <summary>
    /// Имя входящего правила Windows Firewall для HTTPS-порта WEB.
    /// </summary>
    public string HttpsFirewallRuleName { get; set; } = "TechMES WEB HTTPS 7163";

    /// <summary>
    /// Папка, куда Maintenance кладет PFX-сертификат для Kestrel и CER-файл для установки доверия на клиентах.
    /// </summary>
    public string CertificateDirectory { get; set; } = @"C:\TechMES\certs";

    /// <summary>
    /// Имя PFX-файла с закрытым ключом. Этот файл подключается в TechMES.Web/appsettings.json.
    /// </summary>
    public string CertificateFileName { get; set; } = "techmes-web.pfx";

    /// <summary>
    /// Имя публичного CER-файла. Его можно импортировать на планшет, чтобы Chrome доверял HTTPS.
    /// </summary>
    public string PublicCertificateFileName { get; set; } = "techmes-web.cer";

    /// <summary>
    /// Пароль PFX. На текущем этапе секреты храним открыто, как и остальные серверные настройки проекта.
    /// </summary>
    public string CertificatePassword { get; set; } = "TechMES-local-dev";

    /// <summary>
    /// Отображаемое имя self-signed сертификата.
    /// </summary>
    public string CertificateSubject { get; set; } = "TechMES WEB";

    /// <summary>
    /// Включать ли принудительный redirect HTTP -> HTTPS. По умолчанию выключено, чтобы планшет мог работать по HTTP,
    /// пока CER-файл не установлен как доверенный.
    /// </summary>
    public bool EnableHttpsRedirection { get; set; }
}
