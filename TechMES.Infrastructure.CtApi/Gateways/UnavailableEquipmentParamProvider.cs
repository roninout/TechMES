using TechMES.Application.Param;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Заглушка Param-провайдера для режима, когда реальный CtApi/Param недоступен.
/// Runtime.Service может стартовать и показывать понятную ошибку в UI вместо падения приложения.
/// </summary>
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

    /// <summary>
    /// Возвращает отрицательный результат общей проверки числового тега, когда CtApi/Param provider недоступен.
    /// Контракт остаётся тем же для всех потребителей: Tune, Density и других будущих модулей.
    /// </summary>
    public Task<ParamTagCheckResponse> CheckNumericTagAsync(string tagName, bool requireTrend, CancellationToken ct = default)
    {
        return Task.FromResult(new ParamTagCheckResponse
        {
            TagName = (tagName ?? "").Trim(),
            Found = false,
            TrendRequired = requireTrend,
            TrendFound = false,
            Message = _message
        });
    }

    public Task<ParamTuneRuntimeResponse> GetTuneRuntimeAsync(
        EquipmentDto equipment,
        ParamTuneSettingsResponse settings,
        int windowMinutes = 30,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var to =
            NormalizeUtc(toUtc)
            ?? DateTime.UtcNow;

        var from =
            NormalizeUtc(fromUtc)
            ?? to.AddMinutes(
                -Math.Max(1, windowMinutes));

        settings.EquipmentName = equipment.Name;

        return Task.FromResult(new ParamTuneRuntimeResponse
        {
            EquipmentName = equipment.Name,
            TypeGroup = equipment.TypeGroup,
            Supported = false,
            Message = _message,
            Settings = settings,
            Trend = new ParamTrendResponse
            {
                EquipmentName = equipment.Name,
                TypeGroup = equipment.TypeGroup,
                Supported = false,
                Message = _message,
                FromUtc = from,
                ToUtc = to
            }
        });
    }

    public Task<ParamTrendResponse> GetTrendAsync(
        EquipmentDto equipment,
        int windowMinutes = 30,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var to =
            NormalizeUtc(toUtc)
            ?? DateTime.UtcNow;

        var from =
            NormalizeUtc(fromUtc)
            ?? to.AddMinutes(
                -Math.Max(1, windowMinutes));

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

    public Task<ParamPlcRefsResponse> GetPlcRefsAsync(
        EquipmentDto equipment,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ParamPlcRefsResponse
        {
            EquipmentName = equipment.Name,
            Supported = false,
            Message = _message,
            Time = DateTime.Now
        });
    }

    public Task<ParamDiDoRefsResponse> GetDiDoRefsAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ParamDiDoRefsResponse
        {
            EquipmentName = equipment.Name,
            Supported = false,
            Message = _message,
            Time = DateTime.Now
        });
    }

    public Task<ParamDryRunResponse> GetDryRunAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ParamDryRunResponse
        {
            EquipmentName = equipment.Name,
            Supported = false,
            Message = _message,
            Time = DateTime.Now
        });
    }

    public Task<ParamAtvRefResponse> GetAtvRefAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ParamAtvRefResponse
        {
            EquipmentName = equipment.Name,
            Supported = false,
            Message = _message,
            Time = DateTime.Now
        });
    }

    public Task<ParamWriteResponse> WriteAsync(
        EquipmentDto equipment,
        ParamWriteRequest request,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ParamWriteResponse
        {
            EquipmentName = equipment.Name,
            TypeGroup = equipment.TypeGroup,
            ItemName = request.ItemName,
            Success = false,
            Error = _message
        });
    }

    private static DateTime? NormalizeUtc(
        DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : value.Value.ToUniversalTime();
    }
}
