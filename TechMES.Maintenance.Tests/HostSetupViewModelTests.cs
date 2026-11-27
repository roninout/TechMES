using TechMES.Contracts.Host;
using TechMES.Maintenance.ViewModels;

namespace TechMES.Maintenance.Tests;

public sealed class HostSetupViewModelTests
{
    [Theory]
    [InlineData("192.168.3.11", "192.168.3.12", true)]
    [InlineData("192.168.3.12", "192.168.3.11", false)]
    [InlineData("192.168.3.99", "192.168.3.100", true)]
    [InlineData("192.168.3.11", "192.168.3.11", null)]
    [InlineData("192.168.3.11", "", null)]
    public void RoleUsesTheSameAddressOrderAsHost(string node, string peer, bool? coordinator)
    {
        var view = new HostSetupViewModel { Shared = new() { NodeAddress = node, PeerAddress = peer } };

        Assert.Equal(coordinator, view.IsCoordinator);
    }

    [Fact]
    public void RoleAndAddressBindingsFollowLoadingAndEditingTheProfile()
    {
        var view = new HostSetupViewModel();
        var changes = new List<string?>();
        view.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        view.Shared = new HostSharedAddressSettings { NodeAddress = "192.168.3.11", PeerAddress = "192.168.3.12" };

        Assert.Contains(nameof(view.NodeAddress), changes);
        Assert.Contains(nameof(view.PeerAddress), changes);
        Assert.Contains(nameof(view.IsCoordinator), changes);
        Assert.Equal(true, view.IsCoordinator);

        changes.Clear();
        view.NodeAddress = "192.168.3.13";
        view.VirtualAddress = "192.168.3.100";

        Assert.Equal(false, view.IsCoordinator);
        Assert.Contains(nameof(view.NodeTitle), changes);
        Assert.Contains(nameof(view.AddressSummary), changes);
        Assert.Contains("VIP 192.168.3.100", view.AddressSummary);
    }
}
