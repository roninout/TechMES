using TechMES.Application.Param;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;

namespace TechMES.Infrastructure.CtApi.Gateways;

public sealed class UnavailableEquipmentParamProvider : IEquipmentParamProvider
{
    private readonly string _message;

    public UnavailableEquipmentParamProvider(string message)
    {
        _message = message;
    }

    public Task<ParamSnapshotResponse> GetSnapshotAsync(
        EquipmentDto equipment,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ParamSnapshotResponse
        {
            EquipmentName = equipment.Name,
            TypeName = equipment.TypeName,
            TypeGroup = equipment.TypeGroup,
            Supported = false,
            Message = _message,
            Time = DateTime.Now
        });
    }

    public Task<ParamTrendResponse> GetTrendAsync(
        EquipmentDto equipment,
        int windowMinutes = 30,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var to = NormalizeUtc(toUtc) ?? DateTime.UtcNow;
        var from = NormalizeUtc(fromUtc) ?? to.AddMinutes(-Math.Max(1, windowMinutes));

        return Task.FromResult(new ParamTrendResponse
        {
            EquipmentName = equipment.Name,
            TypeGroup = equipment.TypeGroup,
            Supported = false,
            Message = _message,
            FromUtc = from,
            ToUtc = to
        });
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : value.Value.ToUniversalTime();
    }
}
