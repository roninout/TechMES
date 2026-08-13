using TechMES.Application.Calc;
using TechMES.Contracts.Calc;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Используется, когда CtApi отключён или работает Mock provider.
/// </summary>
public sealed class UnavailableCalcModelCatalogProvider(string reason) : ICalcModelCatalogProvider
{
    public Task<CalcModelCatalogResponse> GetSnapshotAsync(CancellationToken ct = default)
    {
        return Task.FromResult(CreateResponse());
    }

    public Task<CalcModelCatalogResponse> ReloadAsync(CancellationToken ct = default)
    {
        return Task.FromResult(CreateResponse());
    }

    private CalcModelCatalogResponse CreateResponse()
    {
        return new CalcModelCatalogResponse
        {
            IsAvailable = false,
            IsLoaded = false,
            ErrorMessage = reason
        };
    }
}