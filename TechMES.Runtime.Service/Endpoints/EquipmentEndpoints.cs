using TechMES.Application.Equipment;

namespace TechMES.Runtime.Service.Endpoints;

public static class EquipmentEndpoints
{
    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/equipment", GetEquipmentListAsync);
        app.MapGet("/api/equipment/{name}", GetEquipmentByNameAsync);
        app.MapPut("/api/equipment/{name}/favorite", SetEquipmentFavoriteAsync);

        return app;
    }

    private static async Task<IResult> GetEquipmentListAsync(
        IEquipmentCatalogProvider equipmentCatalog,
        CancellationToken ct)
    {
        // WEB вызывает этот endpoint, чтобы получить каталог оборудования.
        // Конкретный provider выбирается настройкой EquipmentCatalog:Provider.
        var response = await equipmentCatalog.GetEquipmentListAsync(ct);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetEquipmentByNameAsync(
        string name,
        IEquipmentCatalogProvider equipmentCatalog,
        CancellationToken ct)
    {
        // Получить одно оборудование по имени. Имя может содержать точки: S01.H01.P01.
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(name, ct);

        return equipment is null
            ? Results.NotFound()
            : Results.Ok(equipment);
    }

    private static async Task<IResult> SetEquipmentFavoriteAsync(
        string name,
        EquipmentFavoriteRequest request,
        IEquipmentCatalogProvider equipmentCatalog,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.SetFavoriteAsync(name, request.IsFavorite, ct);

        return equipment is null
            ? Results.NotFound()
            : Results.Ok(equipment);
    }

    private sealed record EquipmentFavoriteRequest(bool IsFavorite);
}
