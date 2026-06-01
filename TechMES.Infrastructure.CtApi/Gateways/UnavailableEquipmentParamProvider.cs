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
    /// <summary>
    /// Текст причины недоступности, который возвращается во все Param-ответы.
    /// </summary>
    private readonly string _message;

    /// <summary>
    /// Создает заглушку с общей диагностической причиной недоступности.
    /// </summary>
    public UnavailableEquipmentParamProvider(string message)
    {
        _message = message;
    }

    /// <summary>
    /// Возвращает неподдерживаемый snapshot без обращения к SCADA.
    /// </summary>
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
    /// Возвращает пустой trend-ответ с корректным временным диапазоном для UI.
    /// </summary>
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

    /// <summary>
    /// Возвращает пустую PLC reference page, если Param-провайдер выключен.
    /// </summary>
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

    /// <summary>
    /// Возвращает пустую DI/DO reference page, если Param-провайдер выключен.
    /// </summary>
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

    /// <summary>
    /// Возвращает пустую DryRun reference page, если Param-провайдер выключен.
    /// </summary>
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

    /// <summary>
    /// Возвращает пустую ATV reference page, если Param-провайдер выключен.
    /// </summary>
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

    /// <summary>
    /// Безопасно запрещает Param write, потому что реальный провайдер недоступен.
    /// </summary>
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

    /// <summary>
    /// Нормализует входные границы тренда в UTC, чтобы UI получал предсказуемый диапазон.
    /// </summary>
    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : value.Value.ToUniversalTime();
    }
}
