using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;

namespace TechMES.Application.Param;

/// <summary>
/// Абстракция чтения и записи Param-данных оборудования.
/// Runtime.Service работает только с этим интерфейсом: реальный источник может быть CtApi,
/// mock/unavailable provider или будущая реализация поверх другого источника.
/// </summary>
public interface IEquipmentParamProvider
{
    /// <summary>
    /// Читает текущий снимок параметров выбранного оборудования:
    /// значения, единицы измерения, доступность вкладок и признак CanWrite для UI.
    /// </summary>
    Task<ParamSnapshotResponse> GetSnapshotAsync(
        EquipmentDto equipment,
        CancellationToken ct = default);

    /// <summary>
    /// Читает исторические trend-точки для графика Param.
    /// WEB может передать фиксированный диапазон UTC, когда пользователь прокручивает историю.
    /// </summary>
    Task<ParamTrendResponse> GetTrendAsync(
        EquipmentDto equipment,
        int windowMinutes = 30,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Читает PLC reference page: связанные PLC-теги и их read-only значения.
    /// </summary>
    Task<ParamPlcRefsResponse> GetPlcRefsAsync(
        EquipmentDto equipment,
        CancellationToken ct = default);

    /// <summary>
    /// Читает DI/DO reference page, используя полный каталог оборудования для разрешения связей.
    /// </summary>
    Task<ParamDiDoRefsResponse> GetDiDoRefsAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default);

    /// <summary>
    /// Читает DryRun reference page: собственные dry-run параметры и связанные DI.
    /// </summary>
    Task<ParamDryRunResponse> GetDryRunAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default);

    /// <summary>
    /// Читает ATV reference page для оборудования, которое ссылается на частотник.
    /// </summary>
    Task<ParamAtvRefResponse> GetAtvRefAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default);

    /// <summary>
    /// Выполняет запись одного разрешенного Param item.
    /// Реализация обязана повторно проверить allow-list, режим DryRun/AllowWrites и audit.
    /// </summary>
    Task<ParamWriteResponse> WriteAsync(
        EquipmentDto equipment,
        ParamWriteRequest request,
        CancellationToken ct = default);
}
