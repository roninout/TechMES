using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Собирает сетевую диагностику локальной машины.
/// Здесь нет изменения настроек Windows: сервис только читает IP-адреса и слушающие TCP-порты.
/// </summary>
public sealed class ServerNetworkService
{
    /// <summary>
    /// Возвращает IPv4-адреса активных сетевых адаптеров и сразу формирует HTTP/HTTPS URL TechMES.Web.
    /// Loopback-адреса скрываем, потому что с планшета они бесполезны.
    /// </summary>
    public IReadOnlyList<ServerAddressInfo> GetWebAddresses(
        int webPort,
        int httpsPort)
    {
        return GetLocalIPv4Addresses()
            .Select(address => new ServerAddressInfo
            {
                AdapterName = address.AdapterName,
                Address = address.Address.ToString(),
                WebUrl = $"http://{address.Address}:{webPort}/",
                HttpsUrl = $"https://{address.Address}:{httpsPort}/"
            })
            .OrderBy(address => address.AdapterName)
            .ThenBy(address => address.Address)
            .ToList();
    }

    /// <summary>
    /// Возвращает IPv4-адреса, которые можно добавить в Subject Alternative Name сертификата.
    /// Имя адаптера сохраняем отдельно, чтобы этот же метод использовать для таблицы адресов.
    /// </summary>
    public IReadOnlyList<(string AdapterName, IPAddress Address)> GetLocalIPv4Addresses()
    {
        return NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter
                .GetIPProperties()
                .UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(address => !IPAddress.IsLoopback(address.Address))
                .Select(address => (adapter.Name, address.Address)))
            .ToList();
    }

    /// <summary>
    /// Проверяет, слушает ли какой-либо процесс указанный TCP-порт.
    /// Это быстрый локальный индикатор, что Kestrel поднялся на нужном порту.
    /// </summary>
    public bool IsTcpPortListening(int port)
    {
        return IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);
    }
}
