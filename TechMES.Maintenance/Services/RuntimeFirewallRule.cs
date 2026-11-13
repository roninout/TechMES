using System.Net;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Подготавливает ограниченное правило Runtime API без обращения к Windows.
/// Имя совпадает с ранее созданным вручную правилом.
/// </summary>
public static class RuntimeFirewallRule
{
    public const string Name = "TechMES-Runtime-HTTP-5101-WebPair";

    /// <summary>
    /// Определяет TCP-порт Runtime и проверяет наличие сетевого listener.
    /// Несколько разных портов требуют отдельной настройки правил.
    /// </summary>
    public static int GetPort(string urls)
    {
        var endpoints = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value =>
        {
            var normalized = value.Replace("://*:", "://0.0.0.0:").Replace("://+:", "://0.0.0.0:");

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Port < 1)
                throw new InvalidOperationException("Runtime URLs must contain valid HTTP/HTTPS listener addresses.");

            return uri;
        }).ToArray();

        var ports = endpoints.Select(uri => uri.Port).Distinct().ToArray();

        if (ports.Length != 1 || endpoints.All(uri => uri.IsLoopback))
            throw new InvalidOperationException("Specify one Runtime TCP port and a network listener in Runtime URLs, then save settings.");

        return ports[0];
    }

    /// <summary>
    /// Принимает отдельные IPv4/IPv6-адреса.
    /// Пустой список, Any, подсети и имена компьютеров отклоняются.
    /// </summary>
    public static string[] ParseAddresses(string value)
    {
        var values = value.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (values.Length == 0)
            throw new InvalidOperationException("Enter the IP addresses of the WEB computers first.");

        return values.Select(item =>
        {
            if (!IPAddress.TryParse(item, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
                throw new InvalidOperationException($"Invalid WEB computer IP address: {item}");

            return ip.ToString();
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Создаёт команду настройки постоянного локального правила.
    /// После записи проверяет направление, действие, порт и разрешённые адреса.
    /// </summary>
    public static string BuildScript(int port, string remoteAddresses)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var addresses = string.Join("','", ParseAddresses(remoteAddresses));

        return $$"""
        $ErrorActionPreference = 'Stop'
        try {
            $parameters = @{ Name='{{Name}}'; PolicyStore='PersistentStore'; Enabled='True'; Direction='Inbound'; Action='Allow'; Protocol='TCP'; LocalPort={{port}}; RemoteAddress=@('{{addresses}}'); Profile='Any' }

            $existing = @(Get-NetFirewallRule -Name $parameters.Name -PolicyStore PersistentStore -ErrorAction SilentlyContinue)

            if ($existing.Count -gt 0) {
                Set-NetFirewallRule @parameters
            } else {
                New-NetFirewallRule @parameters -DisplayName 'TechMES Runtime HTTP from WEB servers' | Out-Null
            }

            $rule = Get-NetFirewallRule -Name $parameters.Name -PolicyStore PersistentStore
            $portFilter = $rule | Get-NetFirewallPortFilter
            $addressFilter = $rule | Get-NetFirewallAddressFilter

            $expected = @($parameters.RemoteAddress | Sort-Object -Unique)
            $actual = @($addressFilter.RemoteAddress | ForEach-Object { $_ -replace '/32$','' -replace '/128$','' } | Sort-Object -Unique)

            if ($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $portFilter.Protocol -notin @('TCP','6') -or [string]$portFilter.LocalPort -ne '{{port}}' -or @(Compare-Object $expected $actual).Count -ne 0) {
                throw 'The saved Runtime firewall rule did not match the requested parameters.'
            }

            Write-Output ('Configured: ' + $parameters.Name)
            Write-Output ('TCP port: {{port}}; WEB addresses: ' + ($actual -join ', '))
            exit 0
        } catch {
            Write-Error $_ -ErrorAction Continue
            exit 1
        }
        """;
    }
}