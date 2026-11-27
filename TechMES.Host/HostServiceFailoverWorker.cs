using System.Diagnostics;
using System.Text;
using TechMES.Contracts.Host;

namespace TechMES.Host;

internal sealed class HostServiceFailoverOptions
{
    public bool Enabled { get; init; }
    public string PreferredComputerName { get; init; } = "";
    public int FailureThreshold { get; init; } = 4;
    public int RecoveryThreshold { get; init; } = 12;

    public void Validate(bool controlEnabled, bool preferred)
    {
        if (!Enabled)
            return;

        if (!controlEnabled || FailureThreshold is < 2 or > 60 || RecoveryThreshold is < 2 or > 60
            || (!preferred && PreferredComputerName.Length > 0 && (Uri.CheckHostName(PreferredComputerName) != UriHostNameType.Dns
                || !PreferredComputerName.Contains('.') || PreferredComputerName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-'))))
            throw new InvalidOperationException("HostServiceFailover requires HostControl and valid thresholds. Windows recovery, if configured, requires the preferred host FQDN.");
    }
}

// Отдельная политика для отказа Host. Проверки отказа WEB продолжаются в HostOwnerWorker.
internal sealed class HostServiceFailoverWorker(HostOwnerEngine owner, IHostOwnerPeer peer, HostSharedAddressSettings shared,
    HostServiceFailoverOptions options, Func<CancellationToken, Task>? recoverPreferredHost, ILogger<HostServiceFailoverWorker> logger) : BackgroundService
{
    private int _failures;
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
                _successes = 0;
                logger.LogWarning(ex, "Host service VIP transfer blocked.");
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

        try
        {
            await peer.StatusAsync();
            _failures = 0;
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _failures = Math.Min(_failures + 1, options.FailureThreshold);
        }

        // Недоступность Host — сигнал для вызова координатора через Windows.
        // Локального захвата VIP по таймауту здесь нет.
        if (recoverPreferredHost is null || _failures < options.FailureThreshold || local.Grant.Assigned || local.AddressPresent
            || !local.WebReady || !local.NetworkReady || DateTimeOffset.UtcNow < _nextRecoveryAttempt)
            return;

        _nextRecoveryAttempt = DateTimeOffset.UtcNow.AddSeconds(60);
        await recoverPreferredHost(ct);
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

    // Имя проверено в настройках. Endpoint предоставляет одну операцию с фиксированными параметрами.
    internal static async Task RequestRecoveryAsync(string computerName, CancellationToken ct)
    {
        var script = $$"""
        $ErrorActionPreference='Stop'
        $session=New-PSSessionOption -OpenTimeout 5000 -OperationTimeout 90000
        Invoke-Command -ComputerName '{{computerName}}' -Authentication Kerberos -ConfigurationName 'TechMES.HostFailover' -SessionOption $session -ScriptBlock { Invoke-TechMESHostVipHandoff }
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
            throw new InvalidOperationException("Windows Host handoff: " + await error + await output);
    }
}
