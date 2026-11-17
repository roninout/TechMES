using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TechMES.Contracts.Host;

// Epoch повышается отдельно при отзыве старого и при выдаче нового разрешения.
public sealed record HostOwnerJournal(long Epoch = 0, string Owner = "", string Target = "", string Phase = "Empty", Guid Operation = default, bool AutomaticWebFailover = false);

public sealed record HostNodeGrant(long Epoch = 0, bool Assigned = false, bool Managed = false);

public sealed record HostOwnerRequest(string Target, long ExpectedEpoch, Guid Operation);

public sealed record HostApplyGrant(string Pair, long Epoch, bool Assigned);

public sealed record HostOwnerStatus(string Pair, string Node, string Coordinator, bool WebReady, bool NetworkReady, string[] Errors, bool AddressPresent, bool AddressReady, HostNodeGrant Grant, HostOwnerJournal Journal, HostWebFailoverStatus? WebFailover = null);

/// <summary>Общая идентичность пары без зависимости от роли Runtime.</summary>
public static class HostOwnerIdentity
{
    /// <summary>Постоянный координатор плановых операций: меньший IPv4 в числовом порядке.</summary>
    public static string Coordinator(HostSharedAddressSettings settings) =>
        Number(settings.NodeAddress) < Number(settings.PeerAddress)
            ? settings.NodeAddress
            : settings.PeerAddress;

    /// <summary>Привязывает журнал к адресам пары, общему IP, префиксу и сертификатам.</summary>
    public static string Pair(HostSharedAddressSettings settings, HostControlSettings control)
    {
        var nodes = new[]
        {
            settings.NodeAddress + ":" + control.CertificateSha256.ToUpperInvariant(),
            settings.PeerAddress + ":" + control.PeerCertificateSha256.ToUpperInvariant()
        }.Order(StringComparer.Ordinal);

        var identity = string.Join("|", nodes) + "|" + settings.VirtualAddress + "/" + settings.PrefixLength;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static uint Number(string value) =>
        BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(value).GetAddressBytes());
}