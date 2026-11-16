using TechMES.Contracts.Host;

namespace TechMES.Host.Tests;

public sealed class HostNetworkTests
{
    private const string AdapterId = "40f57f29-de5a-4705-917f-f7ae8f3314cf";
    private static HostSharedAddressSettings Settings(string vip = "10.20.30.200") => new() { InterfaceId = AdapterId, NodeAddress = "10.20.30.11", PeerAddress = "10.20.30.12", VirtualAddress = vip, PrefixLength = 24 };
    private static HostNetworkSnapshot Network(bool? dhcp = false, bool up = true) => new("test", DateTimeOffset.UtcNow, [new(AdapterId, "Ethernet", up, dhcp, [new("10.20.30.11", 24)], "")], "");

    [Fact]
    public void StaticPairInOneSubnetPassesWithoutAssigningAddress()
    {
        var report = HostNetworkPolicy.Check(Settings(), Network());
        Assert.True(report.LocalChecksPassed);
        Assert.NotEmpty(report.Notes);
    }
    [Theory]
    [InlineData("10.20.30.11")]
    [InlineData("10.20.30.12")]
    [InlineData("10.20.31.200")]
    [InlineData("10.20.30.0")]
    [InlineData("10.20.30.255")]
    [InlineData("224.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("010.20.30.200")]
    [InlineData("10.20.30")]
    [InlineData("10.20.30.200;command")]
    public void InvalidVirtualAddressBlocksPreparation(string vip) => Assert.False(HostNetworkPolicy.Check(Settings(vip), Network()).LocalChecksPassed);

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void DhcpOrUnknownDhcpBlocksPreparation(bool? dhcp) => Assert.Contains(HostNetworkPolicy.Check(Settings(), Network(dhcp)).Errors, error => error.Contains("DHCP"));

    [Fact]
    public void DownInterfaceBlocksPreparation() => Assert.False(HostNetworkPolicy.Check(Settings(), Network(up: false)).LocalChecksPassed);

    [Fact]
    public void WrongPrefixBlocksPreparation()
    {
        var settings = Settings(); settings.PrefixLength = 16;
        Assert.False(HostNetworkPolicy.Check(settings, Network()).LocalChecksPassed);
    }
    [Fact]
    public void AddressAlreadyOnAnotherAdapterBlocksPreparation()
    {
        var network = Network();
        network = network with { Adapters = [.. network.Adapters, new(Guid.NewGuid().ToString(), "other", true, false, [new("10.20.30.200", 24)], "")] };
        Assert.False(HostNetworkPolicy.Check(Settings(), network).LocalChecksPassed);
    }
    [Fact]
    public void UnknownInterfaceBlocksPreparation()
    {
        var settings = Settings(); settings.InterfaceId = Guid.NewGuid().ToString();
        Assert.False(HostNetworkPolicy.Check(settings, Network()).LocalChecksPassed);
    }
    [Theory]
    [InlineData("10.20.30.0")]
    [InlineData("10.20.30.255")]
    public void PeerMustBeUsableHostAddress(string address)
    {
        var settings = Settings(); settings.PeerAddress = address;
        Assert.False(HostNetworkPolicy.Check(settings, Network()).LocalChecksPassed);
    }
    [Fact]
    public void TentativeOrDuplicateNodeAddressIsNotReady()
    {
        var network = Network();
        network = network with { Adapters = [new(AdapterId, "Ethernet", true, false, [new("10.20.30.11", 24, false)], "")] };
        Assert.False(HostNetworkPolicy.Check(Settings(), network).LocalChecksPassed);
    }
    [Fact]
    public void DisabledControlNeedsNoCertificate() => new HostControlSettings().Validate(5116);

    [Theory]
    [InlineData(5116, "A", "B")]
    [InlineData(5117, "A", "A")]
    [InlineData(5117, "A", "a")]
    [InlineData(5117, "", "B")]
    public void UnsafeControlSettingsAreRejected(int port, string own, string peer)
    {
        var settings = new HostControlSettings { Enabled = true, Port = port, CertificateSha256 = string.Concat(Enumerable.Repeat(own, 64)), PeerCertificateSha256 = string.Concat(Enumerable.Repeat(peer, 64)) };
        Assert.Throws<InvalidOperationException>(() => settings.Validate(5116));
    }
}
