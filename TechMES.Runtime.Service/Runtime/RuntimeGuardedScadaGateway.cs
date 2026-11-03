using TechMES.Application.Scada;
using TechMES.Contracts.Scada;

namespace TechMES.Runtime.Service.Runtime;

/// <summary>
/// Защищает обращения к SCADA, в том числе из фоновых компонентов.
/// Внутренний adapter уничтожает DI-контейнер; decorator повторно его не уничтожает.
/// </summary>
public sealed class RuntimeGuardedScadaGateway(IPlantScadaGateway inner, RuntimeActivityState activity) : IPlantScadaGateway, IPlantScadaHealthMonitor
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var lease = activity.AcquireInitialization();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

        await inner.InitializeAsync(stop.Token);
    }

    // Существующие adapter-ы возвращают здесь готовый снимок состояния.
    public Task<PlantScadaHealthResponse> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);

    public async Task<ScadaTagReadResponse> ReadTagAsync(string tagName, CancellationToken ct = default)
    {
        using var lease = activity.Acquire();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

        return await inner.ReadTagAsync(tagName, stop.Token);
    }

    public async Task<ScadaTagBatchReadResponse> ReadTagsAsync(IReadOnlyCollection<string> tagNames, CancellationToken ct = default)
    {
        using var lease = activity.Acquire();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

        return await inner.ReadTagsAsync(tagNames, stop.Token);
    }

    public async Task<ScadaTagWriteResponse> WriteTagAsync(ScadaTagWriteRequest request, CancellationToken ct = default)
    {
        using var lease = activity.Acquire();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

        return await inner.WriteTagAsync(request, stop.Token);
    }

    public async Task RefreshHealthAsync(CancellationToken ct = default)
    {
        using var lease = activity.Acquire();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

        if (inner is IPlantScadaHealthMonitor monitor)
            await monitor.RefreshHealthAsync(stop.Token);
    }
}