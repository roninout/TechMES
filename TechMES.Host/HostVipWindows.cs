using System.Diagnostics;
using System.Text;
using TechMES.Contracts.Host;

namespace TechMES.Host;

/// <summary>Изменяет только один VIP в ActiveStore; основные адреса, gateway и DNS не редактируются.</summary>
public sealed class HostVipWindows(HostSharedAddressSettings settings) : IHostVip
{
    public HostNetworkSnapshot Read() => HostNetworkInspector.Read();

    /// <summary>Адреса и GUID проверяются до генерации PowerShell; DHCP повторно проверяется внутри команды.</summary>
    public static string Script(HostSharedAddressSettings settings, bool assigned)
    {
        if (!Guid.TryParse(settings.InterfaceId, out var id)
            || !HostNetworkPolicy.TryAddress(settings.VirtualAddress, out var vip)
            || !HostNetworkPolicy.TryAddress(settings.NodeAddress, out var node)
            || vip.Equals(node)
            || settings.PrefixLength is < 1 or > 30)
        {
            throw new InvalidOperationException("Некорректный профиль VIP.");
        }

        var common = $$"""
        $ErrorActionPreference='Stop'
        $targetGuid=[guid]'{{id:D}}'
        $adapters=@(Get-NetAdapter -IncludeHidden)
        $nic=@($adapters | Where-Object {
            $candidate=[guid]::Empty
            [guid]::TryParse([string]$_.InterfaceGuid,[ref]$candidate) -and $candidate -eq $targetGuid
        })
        if($nic.Count -ne 1) {
            $available=($adapters | ForEach-Object {"$($_.Name)=$($_.InterfaceGuid)"}) -join '; '
            throw "Interface GUID $targetGuid was not found uniquely. Available: $available"
        }
        $index=$nic[0].ifIndex
        $persistent=@(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore PersistentStore | Where-Object {$_.IPAddress -eq '{{vip}}'})
        if($persistent.Count) {throw 'VIP exists in PersistentStore; manual review required'}
        $found=@(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object {$_.IPAddress -eq '{{vip}}'})
        if(@($found | Where-Object {$_.InterfaceIndex -ne $index -or $_.PrefixLength -ne {{settings.PrefixLength}}}).Count) {throw 'VIP exists on another interface or prefix'}
        """ + "\n";

        return common + (assigned ? $$"""
        $ipif=Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4
        if($ipif.Dhcp -ne 'Disabled') {throw 'DHCP must be disabled'}
        $own=@(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object {$_.IPAddress -eq '{{node}}' -and $_.PrefixLength -eq {{settings.PrefixLength}} -and $_.AddressState -eq 'Preferred'})
        if($own.Count -ne 1) {throw 'Node IPv4 is not ready'}
        if(!$found.Count) {New-NetIPAddress -InterfaceIndex $index -IPAddress '{{vip}}' -PrefixLength {{settings.PrefixLength}} -AddressFamily IPv4 -PolicyStore ActiveStore -SkipAsSource $true | Out-Null}
        """ : $$"""
        if($found.Count) {Remove-NetIPAddress -InterfaceIndex $index -IPAddress '{{vip}}' -AddressFamily IPv4 -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop}
        if(@(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object {$_.IPAddress -eq '{{vip}}'}).Count) {throw 'VIP removal not confirmed'}
        """);
    }

    /// <summary>Печатает ошибки обычным UTF-8 текстом, без служебного CLIXML и progress.</summary>
    public static string WrapScript(string script) =>
        "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ProgressPreference='SilentlyContinue'; try {\n"
        + script
        + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";

    /// <summary>Проверяет завершение DAD после назначения и отсутствие VIP после удаления.</summary>
    public async Task SetAsync(bool assigned)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VIP требует Windows.");

        var script = Script(settings, assigned);

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(WrapScript(script))) })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell не запущен.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();

            throw new TimeoutException("Изменение VIP не подтвердилось вовремя. Продолжите сохранённую операцию после проверки состояния.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException("VIP: " + await error + await output);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var network = Read();

            if (network.Error.Length > 0)
                throw new InvalidOperationException(network.Error);

            var selected = network.Adapters
                .Where(adapter => Guid.TryParse(adapter.Id, out var id) && id == Guid.Parse(settings.InterfaceId))
                .ToArray();

            if (selected.Length != 1 || selected[0].Error.Length > 0)
                throw new InvalidOperationException("Не подтверждено чтение выбранного интерфейса после изменения VIP.");

            var addresses = network.Adapters.SelectMany(adapter => adapter.Addresses)
                .Where(address => address.Address == settings.VirtualAddress)
                .ToArray();

            if (!assigned && addresses.Length == 0 || assigned && addresses.Length == 1 && addresses[0].IsPreferred)
                return;

            await Task.Delay(250);
        }

        throw new InvalidOperationException("VIP не достиг ожидаемого состояния Preferred/Absent.");
    }
}