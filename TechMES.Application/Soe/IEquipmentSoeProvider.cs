using TechMES.Contracts.Equipment;
using TechMES.Contracts.Soe;

namespace TechMES.Application.Soe;

public interface IEquipmentSoeProvider
{
    Task<SoeResponse> GetSoeAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        int perTrendMax = 1000,
        int totalMax = 100,
        CancellationToken ct = default);
}
