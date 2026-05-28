using TechMES.Application.Equipment;
using TechMES.Application.Param;

namespace TechMES.Runtime.Service.Endpoints;

public static class ParamEndpoints
{
    public static IEndpointRouteBuilder MapParamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/param/{equipmentName}/snapshot", GetSnapshotAsync);
        app.MapGet("/api/param/{equipmentName}/trend", GetTrendAsync);
        app.MapGet("/api/param/{equipmentName}/refs/plc", GetPlcRefsAsync);
        app.MapGet("/api/param/{equipmentName}/refs/dido", GetDiDoRefsAsync);
        app.MapGet("/api/param/{equipmentName}/refs/dryrun", GetDryRunAsync);
        app.MapGet("/api/param/{equipmentName}/refs/atv", GetAtvRefAsync);

        return app;
    }

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
}
