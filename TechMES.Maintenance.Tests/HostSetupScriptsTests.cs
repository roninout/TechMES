using TechMES.Contracts.Host;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

public sealed class HostSetupScriptsTests
{
    [Theory]
    [InlineData("Any")]
    [InlineData("10.20.30.11';Remove-Item C:\\test")]
    [InlineData("localhost")]
    public void FirewallRejectsBroadOrExecutableAddress(string address)
    {
        var control = new HostControlSettings { Enabled = true, CertificateSha256 = new('A', 64), PeerCertificateSha256 = new('B', 64) };
        Assert.Throws<ArgumentException>(() => HostSetupScripts.Firewall(new() { NodeAddress = address, PeerAddress = "10.20.30.12" }, control, 5116));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("10.20.30.11';Get-Process")]
    [InlineData("10.20.30")]
    public void CertificateRejectsUnparsedAddress(string address) => Assert.Throws<ArgumentException>(() => HostSetupScripts.Certificate(address));

    [Fact]
    public void DisabledControlCannotOpenFirewall() => Assert.Throws<ArgumentException>(() => HostSetupScripts.Firewall(new() { NodeAddress = "10.20.30.11", PeerAddress = "10.20.30.12" }, new(), 5116));
}
