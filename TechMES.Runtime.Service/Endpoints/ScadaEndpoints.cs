using TechMES.Application.Scada;
using TechMES.Contracts.Scada;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Низкоуровневое SCADA API для диагностики и будущего configurator-а.
/// Основные модули WEB обычно используют более высокоуровневые endpoints,
/// например ParamEndpoints, а не читают tags напрямую.
/// </summary>
public static class ScadaEndpoints
{
    /// <summary>
    /// Подключает health, read tag и write tag endpoints выбранного Plant SCADA adapter-а.
    /// </summary>
    public static IEndpointRouteBuilder MapScadaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/scada/health", GetScadaHealthAsync);
        app.MapGet("/api/scada/tags/{tagName}", ReadTagAsync);
        app.MapPost("/api/scada/tags/write", WriteTagAsync);

        return app;
    }

    /// <summary>
    /// Возвращает состояние Plant SCADA adapter-а: provider, connection и ошибку, если она есть.
    /// </summary>
    private static async Task<IResult> GetScadaHealthAsync(
        IPlantScadaGateway plantScadaGateway,
        CancellationToken ct)
    {
        var health = await plantScadaGateway.GetHealthAsync(ct);

        return Results.Ok(health);
    }

    /// <summary>
    /// Читает один tag через выбранный adapter. Используется для диагностики,
    /// потому что бизнес-модули обычно читают агрегированные DTO.
    /// </summary>
    private static async Task<IResult> ReadTagAsync(
        string tagName,
        IPlantScadaGateway plantScadaGateway,
        CancellationToken ct)
    {
        var result = await plantScadaGateway.ReadTagAsync(tagName, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }

    /// <summary>
    /// Записывает один tag через выбранный adapter.
    /// В рабочем Param write-flow используются дополнительные allow-list и audit,
    /// поэтому этот endpoint стоит рассматривать как сервисный/диагностический.
    /// </summary>
    private static async Task<IResult> WriteTagAsync(
        ScadaTagWriteRequest request,
        IPlantScadaGateway plantScadaGateway,
        CancellationToken ct)
    {
        var result = await plantScadaGateway.WriteTagAsync(request, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }
}
