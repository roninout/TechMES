namespace TechMES.Maintenance.Models;

/// <summary>
/// Один сетевой адрес компьютера, на котором запущен TechMES.Web.
/// Maintenance показывает такие строки, чтобы быстро открыть WEB с планшета
/// или другого клиента в той же сети.
/// </summary>
public sealed class ServerAddressInfo
{
    /// <summary>
    /// Имя сетевого адаптера Windows.
    /// </summary>
    public string AdapterName { get; init; } = "";

    /// <summary>
    /// IPv4-адрес адаптера.
    /// </summary>
    public string Address { get; init; } = "";

    /// <summary>
    /// Готовый HTTP URL WEB-интерфейса на этом адресе.
    /// </summary>
    public string WebUrl { get; init; } = "";

    /// <summary>
    /// Готовый HTTPS URL WEB-интерфейса на этом адресе.
    /// </summary>
    public string HttpsUrl { get; init; } = "";
}
