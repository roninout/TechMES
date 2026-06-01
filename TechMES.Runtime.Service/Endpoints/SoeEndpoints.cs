using Microsoft.Extensions.Options;
using TechMES.Application.Equipment;
using TechMES.Application.Soe;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP API SOE-модуля.
/// Runtime.Service разрешает выбранное оборудование и передает полный каталог в provider,
/// потому что SOE может собираться из связанных trend/event источников.
/// </summary>
public static class SoeEndpoints
{
    /// <summary>
    /// Подключает endpoint чтения SOE для одного оборудования.
    /// </summary>
    public static IEndpointRouteBuilder MapSoeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/soe/{equipmentName}", GetSoeAsync);

        return app;
    }

    /// <summary>
    /// Возвращает SOE-события для выбранного оборудования.
    /// perTrendMax/totalMax можно переопределить из query string, иначе используются SoeOptions.
    /// </summary>
    private static async Task<IResult> GetSoeAsync(
        string equipmentName,
        int? perTrendMax,
        int? totalMax,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentSoeProvider soeProvider,
        IOptions<SoeOptions> options,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        if (equipment.IsGroup)
            return Results.BadRequest("SOE is not available for Equipment group nodes.");

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var settings = options.Value;

        var result = await soeProvider.GetSoeAsync(
            equipment,
            catalog.Equipments,
            perTrendMax.GetValueOrDefault(settings.PerTrendMax),
            totalMax.GetValueOrDefault(settings.TotalMax),
            ct);

        return Results.Ok(result);
    }
}
