using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Управляет входящим правилом Windows Firewall для WEB-порта TechMES.
/// Команды выполняются через netsh без PowerShell-скриптов; для изменения правила
/// Maintenance должен быть запущен от имени администратора.
/// </summary>
public sealed class WindowsFirewallManager(ProcessRunner processRunner)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Проверяет, существует ли правило Windows Firewall с указанным именем
    /// и похоже ли оно на правило для нужного TCP-порта.
    /// </summary>
    public async Task<ServiceCommandResult> QueryInboundTcpRuleAsync(
        string ruleName,
        int port,
        CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(
            "netsh",
            ["advfirewall", "firewall", "show", "rule", $"name={ruleName}"],
            CommandTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return new ServiceCommandResult
            {
                Success = false,
                Status = "Missing",
                Details = result.CombinedOutput
            };
        }

        var hasPort = result.CombinedOutput.Contains(port.ToString(), StringComparison.OrdinalIgnoreCase);

        return new ServiceCommandResult
        {
            Success = hasPort,
            Status = hasPort ? "Open" : "Exists, check port",
            Details = result.CombinedOutput
        };
    }

    /// <summary>
    /// Создает или обновляет входящее правило для WEB-порта.
    /// Если правило уже есть, netsh set rule обновляет основные параметры без создания дублей.
    /// </summary>
    public async Task<ServiceCommandResult> EnsureInboundTcpRuleAsync(
        string ruleName,
        int port,
        CancellationToken cancellationToken = default)
    {
        var existingRule = await QueryInboundTcpRuleAsync(ruleName, port, cancellationToken);
        var arguments = existingRule.Status == "Missing"
            ? new[]
            {
                "advfirewall",
                "firewall",
                "add",
                "rule",
                $"name={ruleName}",
                "dir=in",
                "action=allow",
                "protocol=TCP",
                $"localport={port}",
                "profile=any",
                "enable=yes"
            }
            : new[]
            {
                "advfirewall",
                "firewall",
                "set",
                "rule",
                $"name={ruleName}",
                "new",
                "dir=in",
                "action=allow",
                "protocol=TCP",
                $"localport={port}",
                "profile=any",
                "enable=yes"
            };

        var result = await processRunner.RunAsync(
            "netsh",
            arguments,
            CommandTimeout,
            cancellationToken);

        return new ServiceCommandResult
        {
            Success = result.ExitCode == 0,
            Status = result.ExitCode == 0 ? "Open" : "Failed",
            Details = result.CombinedOutput
        };
    }
}
