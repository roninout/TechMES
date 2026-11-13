using System.Text.Json;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Tests;

/// <summary>
/// Проверки настроек и подготовки команды без изменения Windows Firewall.
/// </summary>
public sealed class RuntimeFirewallTests
{
    /// <summary>Порт определяется из listener-настроек Runtime.</summary>
    [Theory]
    [InlineData("http://0.0.0.0:5101", 5101)]
    [InlineData("http://*:5201", 5201)]
    [InlineData("http://+:5301", 5301)]
    [InlineData("https://[::]:7101", 7101)]
    [InlineData("http://127.0.0.1:5101;http://0.0.0.0:5101", 5101)]
    public void ReadsConfiguredPort(string urls, int expected) => Assert.Equal(expected, RuntimeFirewallRule.GetPort(urls));

    /// <summary>Пустая, локальная и неоднозначная конфигурация отклоняются.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:5101")]
    [InlineData("http://127.0.0.1:5101")]
    [InlineData("http://0.0.0.0:5101;https://0.0.0.0:7101")]
    [InlineData("not a listener")]
    public void RejectsInvalidListeners(string urls) => Assert.Throws<InvalidOperationException>(() => RuntimeFirewallRule.GetPort(urls));

    /// <summary>Неправильный ввод не заменяется широким доступом.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Any")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("10.20.30.0/24")]
    [InlineData("web-server")]
    [InlineData("10.20.30.1'; Write-Output injected; '")]
    public void RejectsInvalidScope(string addresses) => Assert.Throws<InvalidOperationException>(() => RuntimeFirewallRule.BuildScript(5101, addresses));

    /// <summary>Дубликаты удаляются, IPv6 нормализуется.</summary>
    [Fact]
    public void NormalizesAddresses() => Assert.Equal(new[] { "10.20.30.1", "2001:db8::1" }, RuntimeFirewallRule.ParseAddresses("10.20.30.1; 2001:0db8::1,10.20.30.1"));

    /// <summary>Команда использует постоянное ограниченное правило с прежним именем.</summary>
    [Fact]
    public void BuildsRestrictedPersistentRule()
    {
        var script = RuntimeFirewallRule.BuildScript(5201, "10.20.30.1,10.20.30.2");

        Assert.Contains("Name='TechMES-Runtime-HTTP-5101-WebPair'", script);
        Assert.Contains("PolicyStore='PersistentStore'", script);
        Assert.Contains("LocalPort=5201", script);
        Assert.Contains("RemoteAddress=@('10.20.30.1','10.20.30.2')", script);
        Assert.Contains("Set-NetFirewallRule @parameters", script);
        Assert.Contains("New-NetFirewallRule @parameters", script);
        Assert.Contains("Get-NetFirewallAddressFilter", script);
    }

    /// <summary>Адреса сохраняются и восстанавливаются вместе с профилем.</summary>
    [Fact]
    public void PersistsAddresses()
    {
        var options = new ServerOptions
        {
            RuntimeFirewallRemoteAddresses = "10.20.30.1,10.20.30.2"
        };

        var loaded = JsonSerializer.Deserialize<ServerOptions>(JsonSerializer.Serialize(options));

        Assert.Equal(options.RuntimeFirewallRemoteAddresses, loaded!.RuntimeFirewallRemoteAddresses);
        Assert.Empty(new ServerOptions().RuntimeFirewallRemoteAddresses);
    }
}