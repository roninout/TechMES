using System.Diagnostics;
using System.Text;
using TechMES.Contracts.Host;

namespace TechMES.Host.Tests;

/// <summary>Запускает настоящий PowerShell с подставными NetTCPIP-командами, без изменения сети.</summary>
public sealed class HostVipPowerShellTests
{
    [PowerShellTheory]
    [InlineData("braced", true)]
    [InlineData("plain", true)]
    [InlineData("guid", true)]
    [InlineData("missing", false)]
    [InlineData("write_error", false)]
    public async Task AdapterGuidFormatsAndPlainErrors(string mode, bool success)
    {
        const string id = "6d81b84e-6e80-40ea-8a93-6c89a5fbc034";

        var adapterGuid = mode switch
        {
            "plain" => "'" + id + "'",
            "guid" => "[guid]'" + id + "'",
            "missing" => "'00000000-0000-0000-0000-000000000001'",
            _ => "'{" + id.ToUpperInvariant() + "}'"
        };

        var settings = new HostSharedAddressSettings
        {
            InterfaceId = "{" + id + "}",
            NodeAddress = "10.20.30.11",
            PeerAddress = "10.20.30.12",
            VirtualAddress = "10.20.30.200",
            PrefixLength = 24
        };

        var mocks = $$"""
        $global:Vip=$false
        $global:Writes=0
        function Get-NetAdapter {
            param([switch]$IncludeHidden)
            [pscustomobject]@{InterfaceGuid={{adapterGuid}};Name='Ethernet';ifIndex=7}
            [pscustomobject]@{InterfaceGuid='not-a-guid';Name='Unrelated';ifIndex=8}
        }
        function Get-NetIPInterface {
            param($InterfaceIndex,$AddressFamily)
            [pscustomobject]@{Dhcp='Disabled'}
        }
        function Get-NetIPAddress {
            param($InterfaceIndex,$AddressFamily,$PolicyStore)
            [pscustomobject]@{IPAddress='10.20.30.11';InterfaceIndex=7;PrefixLength=24;AddressState='Preferred'}
            if($PolicyStore -eq 'ActiveStore' -and $global:Vip) {
                [pscustomobject]@{IPAddress='10.20.30.200';InterfaceIndex=7;PrefixLength=24;AddressState='Preferred'}
            }
        }
        function New-NetIPAddress {
            param($InterfaceIndex,$IPAddress,$PrefixLength,$AddressFamily,$PolicyStore,$SkipAsSource)
            if('{{mode}}' -eq 'write_error') {throw 'Controlled VIP write failure'}
            if($InterfaceIndex -ne 7 -or $IPAddress -ne '10.20.30.200' -or $PrefixLength -ne 24 -or $PolicyStore -ne 'ActiveStore' -or !$SkipAsSource) {throw 'Unexpected add scope'}
            $global:Vip=$true
            $global:Writes++
        }
        function Remove-NetIPAddress {
            param($InterfaceIndex,$IPAddress,$AddressFamily,$PolicyStore,$Confirm,$ErrorAction)
            if($InterfaceIndex -ne 7 -or $IPAddress -ne '10.20.30.200' -or $PolicyStore -ne 'ActiveStore') {throw 'Unexpected remove scope'}
            $global:Vip=$false
            $global:Writes++
        }
        """;

        var script = mocks + "\n" + HostVipWindows.Script(settings, true)
            + "\nif(!$global:Vip -or $global:Writes -ne 1) {throw 'Add not confirmed'}\n"
            + HostVipWindows.Script(settings, false)
            + "\nif($global:Vip -or $global:Writes -ne 2) {throw 'Removal not confirmed'}\n[Console]::WriteLine('VIP TEST OK')";

        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")
            : Environment.GetEnvironmentVariable("TECHMES_TEST_POWERSHELL")!;

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(HostVipWindows.WrapScript(script))) })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException("PowerShell regression test timed out.");
        }

        var output = await stdout;
        var error = await stderr;

        Assert.DoesNotContain("CLIXML", error);
        Assert.DoesNotContain("<Objs", error);

        if (success)
        {
            Assert.True(process.ExitCode == 0, output + error);
            Assert.Contains("VIP TEST OK", output);
            Assert.True(string.IsNullOrWhiteSpace(error), error);
        }
        else
        {
            Assert.Equal(1, process.ExitCode);
            Assert.Contains(mode == "missing" ? "Interface GUID" : "Controlled VIP write failure", error);
            Assert.DoesNotContain("VIP TEST OK", output);
        }
    }
}

/// <summary>На Windows используется штатный PowerShell 5.1; на других ОС нужен явно заданный pwsh.</summary>
public sealed class PowerShellTheoryAttribute : TheoryAttribute
{
    public PowerShellTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TECHMES_TEST_POWERSHELL")))
            Skip = "Нужен Windows PowerShell либо TECHMES_TEST_POWERSHELL для запуска pwsh.";
    }
}