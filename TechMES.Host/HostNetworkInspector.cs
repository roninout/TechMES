using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using TechMES.Contracts.Host;

namespace TechMES.Host;

/// <summary>Читает интерфейсы Windows. Ошибка чтения блокирует готовность, а не превращается в успех.</summary>
public static class HostNetworkInspector
{
    public static HostNetworkSnapshot Read()
    {
        if (!OperatingSystem.IsWindows()) return new(Environment.MachineName, DateTimeOffset.UtcNow, [], "Проверка интерфейсов Host поддерживается на Windows.");
        return ReadWindows();
    }

    [SupportedOSPlatform("windows")]
    private static HostNetworkSnapshot ReadWindows()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback).Select(adapter =>
            {
                try
                {
                    var properties = adapter.GetIPProperties();
                    var addresses = properties.UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork).Select(address => new HostIPv4Address(address.Address.ToString(), address.PrefixLength, address.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)).ToArray();
                    return new HostAdapterInfo(adapter.Id, adapter.Name, adapter.OperationalStatus == OperationalStatus.Up, properties.GetIPv4Properties()?.IsDhcpEnabled, addresses, "");
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    return new HostAdapterInfo(adapter.Id, adapter.Name, false, null, [], "Не удалось прочитать IPv4 интерфейса: " + adapter.Name);
                }
            }).ToArray();
            return new(Environment.MachineName, DateTimeOffset.UtcNow, adapters, "");
        }
        catch (NetworkInformationException)
        {
            return new(Environment.MachineName, DateTimeOffset.UtcNow, [], "Не удалось получить список интерфейсов Windows.");
        }
    }
}
