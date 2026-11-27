using TechMES.Contracts.Host;

namespace TechMES.Host;

/// <summary>Независимый диагностический опрос второго Host для локального WEB.</summary>
internal sealed class HostPeerDiagnosticsWorker(HostOwnerTransport peer, string node, string pair, string coordinator) : BackgroundService
{
    private HostPeerDiagnostics _latest = new(node);

    public HostPeerDiagnostics Latest => Volatile.Read(ref _latest);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                var status = await peer.StatusAsync(stoppingToken);
                if (status.Node != node || status.Coordinator != coordinator || status.Pair != pair
                    || status.Grant is null || status.Journal is null || status.Errors is null)
                    throw new InvalidDataException("Второй Host вернул несогласованный профиль пары.");

                Volatile.Write(ref _latest, new(node, DateTimeOffset.UtcNow, status));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var error = ex is OperationCanceledException ? "Второй Host не ответил за 5 секунд." : ex.Message;
                Volatile.Write(ref _latest, new(node, DateTimeOffset.UtcNow, Error: error));
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
