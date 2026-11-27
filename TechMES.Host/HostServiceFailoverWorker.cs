using System.Diagnostics;
using System.Text;
using TechMES.Contracts.Host;

namespace TechMES.Host;

internal enum HostRecoveryAction
{
    HandoffVip,
    StartPreferredHost
}

// Отдельная политика для отказа Host. Проверки отказа WEB продолжаются в HostOwnerWorker.
internal sealed class HostServiceFailoverWorker(HostOwnerEngine owner, IHostOwnerPeer peer, HostSharedAddressSettings shared,
    HostServiceFailoverOptions options, Func<HostRecoveryAction, CancellationToken, Task>? recoverPreferredHost, ILogger<HostServiceFailoverWorker> logger) : BackgroundService
{
    private int _failures;
    private int _standbyWebFailures;
    private int _successes;
    private long _observedEpoch;
    private DateTimeOffset _nextRecoveryAttempt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ObserveAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _failures = 0;
                _standbyWebFailures = 0;
                _successes = 0;
                logger.LogWarning(ex, "Automatic Host recovery blocked.");
            }
        }
    }

    internal async Task ObserveAsync(CancellationToken ct)
    {
        var local = owner.Status();
        if (local.Node == local.Coordinator)
        {
            await CheckReturnAsync(local, ct);
            return;
        }

        if (recoverPreferredHost is null)
            return;

        try
        {
            await peer.StatusAsync();
            _failures = 0;
            _standbyWebFailures = 0;
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _failures = Math.Min(_failures + 1, options.FailureThreshold);
        }

        // Проверяем актуальный grant после ожидания ответа координатора.
        local = owner.Status();
        var standbyOwnsVip = local.NetworkReady && local.AddressReady && local.Grant is { Assigned: true, Managed: true };
        _standbyWebFailures = standbyOwnsVip && !local.WebReady
            ? Math.Min(_standbyWebFailures + 1, options.FailureThreshold)
            : 0;

        if (_failures < options.FailureThreshold || !local.NetworkReady
            || DateTimeOffset.UtcNow < _nextRecoveryAttempt)
            return;

        HostRecoveryAction action;
        if (_standbyWebFailures >= options.FailureThreshold)
            action = HostRecoveryAction.StartPreferredHost;
        else if (!local.Grant.Assigned && !local.AddressPresent && local.WebReady)
            action = HostRecoveryAction.HandoffVip;
        else
            return;

        // Только координатор передаёт VIP через журнал, в том числе после перезапуска.
        _nextRecoveryAttempt = DateTimeOffset.UtcNow.AddSeconds(60);
        logger.LogInformation("Requesting Windows Host recovery on {Node}: {Action}.", local.Coordinator, action);
        ct.ThrowIfCancellationRequested();
        await recoverPreferredHost(action, ct);
    }

    private async Task CheckReturnAsync(HostOwnerStatus local, CancellationToken ct)
    {
        var journal = local.Journal;
        if (journal is { AutomaticHostTransfer: true, Phase: "Releasing" or "Granting" })
        {
            ct.ThrowIfCancellationRequested();
            await owner.ResumeAsync(journal.Operation);
            return;
        }

        if (journal.Epoch != _observedEpoch)
        {
            _observedEpoch = journal.Epoch;
            _successes = 0;
        }

        // Автовозврат применяется к передаче из-за отказа Host, независимо от политики WEB.
        if (journal is not { Phase: "Stable", AutomaticHostTransfer: true } || journal.Owner != shared.PeerAddress
            || !local.WebReady || !local.NetworkReady)
        {
            _successes = 0;
            return;
        }

        var other = await peer.StatusAsync();
        if (other.Pair != local.Pair || other.Node != shared.PeerAddress || other.Coordinator != local.Node
            || !other.Grant.Assigned || other.Grant.Epoch != journal.Epoch)
        {
            _successes = 0;
            return;
        }

        if (++_successes < options.RecoveryThreshold)
            return;

        _successes = 0;
        ct.ThrowIfCancellationRequested();
        await owner.ChangeAsync(new(local.Node, journal.Epoch, Guid.NewGuid()), false, automaticHostTransfer: true);
    }

    // Имя проверено в настройках. Endpoint предоставляет только фиксированные команды без параметров.
    internal static async Task RequestRecoveryAsync(string computerName, HostRecoveryAction action, CancellationToken ct)
    {
        var command = action switch
        {
            HostRecoveryAction.HandoffVip => "Invoke-TechMESHostVipHandoff",
            HostRecoveryAction.StartPreferredHost => "Start-TechMESPreferredHost",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var script = $$"""
        $ErrorActionPreference='Stop'
        $session=New-PSSessionOption -OpenTimeout 5000 -OperationTimeout 90000
        Invoke-Command -ComputerName '{{computerName}}' -Authentication Kerberos -ConfigurationName 'TechMES.HostFailover' -SessionOption $session -ScriptBlock { {{command}} }
        """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(HostVipWindows.WrapScript(script))) })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запущен.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows Host recovery ({action}): " + await error + await output);
    }
}
