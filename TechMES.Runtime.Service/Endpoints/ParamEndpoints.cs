using TechMES.Application.Equipment;
using TechMES.Application.Param;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP API Param-модуля.
/// Этот слой не знает деталей CtApi: он только находит EquipmentDto в каталоге
/// и передает запрос в IEquipmentParamProvider.
/// </summary>
public static class ParamEndpoints
{
    /// <summary>
    /// Подключает все Param endpoints к Minimal API Runtime.Service.
    /// URL-ы специально сгруппированы под /api/param/{equipmentName}, чтобы WEB
    /// мог работать с одним выбранным оборудованием и разными вкладками Param.
    /// </summary>
    public static IEndpointRouteBuilder MapParamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/param/{equipmentName}/snapshot", GetSnapshotAsync);
        app.MapGet("/api/param/{equipmentName}/trend", GetTrendAsync);
        app.MapGet("/api/param/{equipmentName}/refs/plc", GetPlcRefsAsync);
        app.MapGet("/api/param/{equipmentName}/refs/dido", GetDiDoRefsAsync);
        app.MapGet("/api/param/{equipmentName}/refs/dryrun", GetDryRunAsync);
        app.MapGet("/api/param/{equipmentName}/refs/atv", GetAtvRefAsync);
        app.MapPost("/api/param/{equipmentName}/write", WriteAsync);

        return app;
    }

    /// <summary>
    /// Возвращает текущие Param-значения и список доступных вкладок.
    /// Этот endpoint вызывается периодически, пока пользователь находится в Param-зоне.
    /// </summary>
    private static async Task<IResult> GetSnapshotAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var result = await paramProvider.GetSnapshotAsync(equipment, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает trend-точки для графика.
    /// fromUtc/toUtc используются, когда пользователь прокручивает историю ECharts.
    /// </summary>
    private static async Task<IResult> GetTrendAsync(
        string equipmentName,
        int? windowMinutes,
        DateTime? fromUtc,
        DateTime? toUtc,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var result = await paramProvider.GetTrendAsync(
            equipment,
            windowMinutes.GetValueOrDefault(30),
            fromUtc,
            toUtc,
            ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает PLC reference page для текущего оборудования.
    /// </summary>
    private static async Task<IResult> GetPlcRefsAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var result = await paramProvider.GetPlcRefsAsync(equipment, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает DI/DO reference page. Для разрешения ссылок нужен полный каталог.
    /// </summary>
    private static async Task<IResult> GetDiDoRefsAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var result = await paramProvider.GetDiDoRefsAsync(
            equipment,
            catalog.Equipments,
            ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает DryRun reference page и связанные DI-узлы.
    /// </summary>
    private static async Task<IResult> GetDryRunAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var result = await paramProvider.GetDryRunAsync(
            equipment,
            catalog.Equipments,
            ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает связанную ATV page, если оборудование имеет ссылку на частотник.
    /// </summary>
    private static async Task<IResult> GetAtvRefAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var result = await paramProvider.GetAtvRefAsync(
            equipment,
            catalog.Equipments,
            ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Выполняет запись одного Param item.
    /// Endpoint намеренно повторно разрешает оборудование по имени и не доверяет только данным WEB.
    /// </summary>
    private static async Task<IResult> WriteAsync(
        string equipmentName,
        ParamWriteRequest request,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        IAppRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
        {
            if (!IsDryRunWriteItem(request.ItemName) && !IsReferenceWriteRequest(request))
                return Results.NotFound();

            // DryRun/PLC reference settings can be stored on equipment that is not present in the visible WEB catalog.
            // CtApi can still resolve its EquipItem tag by name; provider-level validation below decides whether the write is allowed.
            equipment = new EquipmentDto
            {
                Name = equipmentName,
                DisplayName = equipmentName,
                TypeGroup = EquipmentTypeGroup.Unknown,
                IsGroup = false
            };
        }

        request.Actor = string.IsNullOrWhiteSpace(request.Actor)
            ? runtimeContext.DeviceName
            : request.Actor;

        var result = await paramProvider.WriteAsync(equipment, request, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }

    /// <summary>
    /// Проверяет DryRun write item names на endpoint-уровне, чтобы разрешить запись reference equipment,
    /// которого может не быть в основном каталоге оборудования.
    /// </summary>
    private static bool IsDryRunWriteItem(string? itemName)
    {
        return itemName is not null
            && (itemName.Equals("DryRunAEn", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("DryRunLimToOff", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("DryRunTimeToOn", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("DryRunTimeToOff", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Проверяет, что WEB просит reference-write из PLC вкладки.
    /// Полное право записи не определяется здесь: CtApi provider заново читает TabPLC
    /// исходного оборудования и ищет там целевую строку.
    /// </summary>
    private static bool IsReferenceWriteRequest(ParamWriteRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ReferenceSourceEquipmentName)
            && request.ReferenceValueKind.HasValue
            && !string.IsNullOrWhiteSpace(request.ItemName);
    }
}
