using TechMES.Application.Soe;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Soe;

namespace TechMES.Infrastructure.CtApi.Gateways;

public sealed class UnavailableEquipmentSoeProvider : IEquipmentSoeProvider
{
    private readonly string _message;

    public UnavailableEquipmentSoeProvider(string message)
    {
        _message = message;
    }

    public Task<SoeResponse> GetSoeAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        int perTrendMax = 1000,
        int totalMax = 100,
        CancellationToken ct = default)
    {
        return Task.FromResult(new SoeResponse
        {
            EquipmentName = equipment.Name,
            Supported = false,
            Message = _message,
            LoadedAt = DateTime.Now
        });
    }
}
