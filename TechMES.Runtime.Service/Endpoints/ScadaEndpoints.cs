using TechMES.Application.Scada;
using TechMES.Contracts.Scada;

namespace TechMES.Runtime.Service.Endpoints;

public static class ScadaEndpoints
{
    public static IEndpointRouteBuilder MapScadaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/scada/health", GetScadaHealthAsync);
        app.MapGet("/api/scada/tags/{tagName}", ReadTagAsync);
        app.MapPost("/api/scada/tags/write", WriteTagAsync);

        return app;
    }

    private static async Task<IResult> GetScadaHealthAsync(
        IPlantScadaGateway plantScadaGateway,
        CancellationToken ct)
    {
        // Проверка состояния Plant SCADA adapter-а.
        // WEB/Configurator будут использовать этот endpoint для диагностики.
        var health = await plantScadaGateway.GetHealthAsync(ct);

        return Results.Ok(health);
    }

    private static async Task<IResult> ReadTagAsync(
        string tagName,
        IPlantScadaGateway plantScadaGateway,
        CancellationToken ct)
    {
        // Чтение одного tag через выбранный Plant SCADA adapter.
        var result = await plantScadaGateway.ReadTagAsync(tagName, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }

    private static async Task<IResult> WriteTagAsync(
        ScadaTagWriteRequest request,
        IPlantScadaGateway plantScadaGateway,
        CancellationToken ct)
    {
        // Запись одного tag.
        // В реальном CtApi adapter-е позже добавим privilege/operator audit.
        var result = await plantScadaGateway.WriteTagAsync(request, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }
}
