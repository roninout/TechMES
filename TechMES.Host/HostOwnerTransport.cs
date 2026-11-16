using System.Net.Http.Json;
using TechMES.Contracts.Host;
using TechMES.Host.Control;

namespace TechMES.Host;

/// <summary>Команды между службами идут только по закреплённому mTLS endpoint второго узла.</summary>
public sealed class HostOwnerTransport(HostSharedAddressSettings settings, HostControlSettings control) : IHostOwnerPeer
{
    public async Task<HostOwnerStatus> StatusAsync()
    {
        using var client = HostNodeCertificates.CreateClient(control, settings.NodeAddress);

        return await client.GetFromJsonAsync<HostOwnerStatus>(Url("status"))
            ?? throw new InvalidDataException("Пустой статус второго Host.");
    }

    public async Task ApplyAsync(HostApplyGrant command)
    {
        using var client = HostNodeCertificates.CreateClient(control, settings.NodeAddress);
        client.Timeout = TimeSpan.FromSeconds(30);

        using var response = await client.PostAsJsonAsync(Url("apply"), command);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Второй Host: " + await response.Content.ReadAsStringAsync());
    }

    private Uri Url(string action) => new UriBuilder("https", settings.PeerAddress, control.Port, "/api/host/owner/" + action).Uri;
}

/// <summary>Восстанавливает существующее разрешение; по таймауту peer не создаёт нового владельца.</summary>
public sealed class HostOwnerWorker(HostOwnerEngine owner, ILogger<HostOwnerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previous = "";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await owner.ReconcileAsync();
                previous = "";
            }
            catch (Exception ex)
            {
                if (previous != ex.Message)
                    logger.LogError(ex, "Host VIP reconciliation blocked");

                previous = ex.Message;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await owner.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}