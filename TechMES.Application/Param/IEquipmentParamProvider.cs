using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;

namespace TechMES.Application.Param;

public interface IEquipmentParamProvider
{
    Task<ParamSnapshotResponse> GetSnapshotAsync(
        EquipmentDto equipment,
        CancellationToken ct = default);

    Task<ParamTrendResponse> GetTrendAsync(
        EquipmentDto equipment,
        int windowMinutes = 30,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);
}
