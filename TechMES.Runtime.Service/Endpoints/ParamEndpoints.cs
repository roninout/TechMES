using TechMES.Application.Equipment;
using TechMES.Application.Param;

namespace TechMES.Runtime.Service.Endpoints;

public static class ParamEndpoints
{
    public static IEndpointRouteBuilder MapParamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/param/{equipmentName}/snapshot", GetSnapshotAsync);
        app.MapGet("/api/param/{equipmentName}/trend", GetTrendAsync);

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
}
