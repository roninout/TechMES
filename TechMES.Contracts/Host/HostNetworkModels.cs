using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TechMES.Contracts.Host;

/// <summary>Подготовка общего адреса. Сохранение этих настроек само по себе не назначает IP.</summary>
public sealed class HostSharedAddressSettings
{
    public string InterfaceId { get; set; } = "";
    public string NodeAddress { get; set; } = "";
    public string PeerAddress { get; set; } = "";
    public string VirtualAddress { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
}

/// <summary>Отдельный mTLS endpoint Host для двух доверенных узлов.</summary>
public sealed class HostControlSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5117;
    public string CertificateSha256 { get; set; } = "";
    public string PeerCertificateSha256 { get; set; } = "";

    /// <summary>Пустой отключённый профиль допустим; включённый требует разных сертификатов узлов.</summary>
    public void Validate(int localHealthPort)
    {
        if (!Enabled) return;
        if (Port is < 1024 or > 65535 || Port == localHealthPort || !Regex.IsMatch(CertificateSha256, "^[0-9A-Fa-f]{64}$") || !Regex.IsMatch(PeerCertificateSha256, "^[0-9A-Fa-f]{64}$") || CertificateSha256.Equals(PeerCertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HostControl: укажите отдельный порт и разные SHA256 сертификатов двух узлов.");
    }
}

public sealed record HostIPv4Address(string Address, int PrefixLength, bool IsPreferred = true);
public sealed record HostAdapterInfo(string Id, string Name, bool IsUp, bool? DhcpEnabled, HostIPv4Address[] Addresses, string Error)
{
    public string Display => $"{Name}: {string.Join(", ", Addresses.Select(address => $"{address.Address}/{address.PrefixLength}"))}; DHCP={DhcpEnabled?.ToString() ?? "Unknown"}";
}
public sealed record HostNetworkSnapshot(string Computer, DateTimeOffset CheckedAtUtc, HostAdapterInfo[] Adapters, string Error);
public sealed record HostNetworkCheck(HostSharedAddressSettings Settings, HostNetworkSnapshot Network, string[] Errors, string[] Notes)
{
    public bool LocalChecksPassed => Errors.Length == 0;
}

/// <summary>Чистые проверки сети: без изменения интерфейсов, DNS или маршрутов.</summary>
public static class HostNetworkPolicy
{
    /// <summary>Принимает только обычную запись unicast IPv4 из четырёх десятичных октетов.</summary>
    public static bool TryAddress(string value, out IPAddress address)
    {
        address = IPAddress.None;
        var parts = (value ?? "").Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9') || !byte.TryParse(part, out _))) return false;
        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = parsed.GetAddressBytes();
        if (bytes[0] is 0 or 127 or >= 224) return false;
        address = parsed;
        return parsed.ToString() == value;
    }

    /// <summary>Сравнивает подсети IPv4 по заданному префиксу.</summary>
    public static bool SameSubnet(IPAddress left, IPAddress right, int prefix)
    {
        if (prefix is < 1 or > 30 || left.AddressFamily != AddressFamily.InterNetwork || right.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = left.GetAddressBytes(); var b = right.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++) if ((a[bit / 8] & (1 << (7 - bit % 8))) != (b[bit / 8] & (1 << (7 - bit % 8)))) return false;
        return true;
    }

    /// <summary>Проверяет выбранный адаптер, DHCP, адреса узлов и доступность адресного пространства.</summary>
    public static HostNetworkCheck Check(HostSharedAddressSettings settings, HostNetworkSnapshot network)
    {
        var errors = new List<string>();
        if (network.Error.Length > 0) errors.Add(network.Error);
        var nodeOk = TryAddress(settings.NodeAddress, out var node);
        var peerOk = TryAddress(settings.PeerAddress, out var peer);
        var vipOk = TryAddress(settings.VirtualAddress, out var vip);
        if (!nodeOk) errors.Add("Укажите собственный IPv4 этого компьютера.");
        if (!peerOk) errors.Add("Укажите IPv4 второго компьютера.");
        if (!vipOk) errors.Add("Укажите выделенный общий IPv4.");
        if (settings.PrefixLength is < 1 or > 30) errors.Add("Префикс должен быть от 1 до 30.");
        if (nodeOk && peerOk && node.Equals(peer) || nodeOk && vipOk && node.Equals(vip) || peerOk && vipOk && peer.Equals(vip)) errors.Add("Адреса двух узлов и общий IP должны различаться.");
        if (nodeOk && peerOk && !SameSubnet(node, peer, settings.PrefixLength)) errors.Add("Оба узла должны находиться в выбранной общей подсети.");
        if (nodeOk && vipOk && !SameSubnet(node, vip, settings.PrefixLength)) errors.Add("Общий IP находится вне выбранной подсети.");
        if (settings.PrefixLength is >= 1 and <= 30)
        {
            foreach (var candidate in new[] { (nodeOk, node), (peerOk, peer), (vipOk, vip) })
            {
                if (!candidate.Item1) continue;
                var bytes = candidate.Item2.GetAddressBytes();
                var hostBits = Enumerable.Range(settings.PrefixLength, 32 - settings.PrefixLength).Select(bit => (bytes[bit / 8] & (1 << (7 - bit % 8))) != 0).ToArray();
                if (hostBits.All(bit => bit) || hostBits.All(bit => !bit)) errors.Add($"Адрес {candidate.Item2} не может быть адресом сети или broadcast.");
            }
        }
        var adapters = network.Adapters.Where(adapter => Guid.TryParse(adapter.Id, out var id) && Guid.TryParse(settings.InterfaceId, out var configured) && id == configured).ToArray();
        if (adapters.Length != 1) errors.Add("Выберите один существующий сетевой интерфейс.");
        else
        {
            var adapter = adapters[0];
            if (!adapter.IsUp) errors.Add("Выбранный интерфейс не поднят.");
            if (adapter.Error.Length > 0) errors.Add(adapter.Error);
            if (adapter.DhcpEnabled != false) errors.Add("Нужен статический IPv4: DHCP включён либо его состояние не подтверждено. Host не меняет DHCP.");
            if (!adapter.Addresses.Any(address => address.Address == settings.NodeAddress && address.PrefixLength == settings.PrefixLength && address.IsPreferred)) errors.Add("Собственный IP с указанным префиксом и состоянием Preferred не найден на выбранном интерфейсе.");
        }
        if (network.Adapters.Any(adapter => adapter.Addresses.Any(address => address.Address == settings.VirtualAddress))) errors.Add("Общий IP уже назначен локально. Подготовка не изменяет существующие адреса.");
        return new(settings, network, errors.Distinct().ToArray(), ["IP не назначается. Нужна такая же проверка на втором узле.", "Эта проверка не доказывает, что IP свободен во всей сети: адрес должен быть выделен администратором и исключён из DHCP."]);
    }
}
