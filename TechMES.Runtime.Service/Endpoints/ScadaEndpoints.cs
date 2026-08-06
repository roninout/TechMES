using TechMES.Application.Scada;
using TechMES.Contracts.Scada;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Низкоуровневое SCADA API Runtime.Service.
/// </summary>
public static class ScadaEndpoints
{
    private const int MaximumBatchTagCount = 500;

    /// <summary>
    /// Подключает health, одиночное и пакетное чтение, а также запись тега.
    /// </summary>
    public static IEndpointRouteBuilder MapScadaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/scada/health", GetScadaHealthAsync);
        app.MapGet("/api/scada/tags/{tagName}", ReadTagAsync);
        app.MapPost("/api/scada/tags/read-batch", ReadTagsAsync);
        app.MapPost("/api/scada/tags/write", WriteTagAsync);

        return app;
    }

    private static async Task<IResult> GetScadaHealthAsync(IPlantScadaGateway gateway, CancellationToken ct)
    {
        return Results.Ok(await gateway.GetHealthAsync(ct));
    }

    private static async Task<IResult> ReadTagAsync(string tagName, IPlantScadaGateway gateway, CancellationToken ct)
    {
        var result = await gateway.ReadTagAsync(tagName, ct);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    /// <summary>
    /// Читает до 500 тегов одним HTTP-запросом.
    ///
    /// Отдельные ошибки возвращаются внутри Items и не изменяют
    /// HTTP status всего корректно сформированного batch-запроса.
    /// </summary>
    private static async Task<IResult> ReadTagsAsync(ScadaTagBatchReadRequest? request, IPlantScadaGateway gateway, CancellationToken ct)
    {
        if (request?.TagNames is null || request.TagNames.Count == 0)
            return BatchError("scada.batch-empty", "At least one SCADA tag is required.");

        if (request.TagNames.Count > MaximumBatchTagCount)
        {
            return BatchError(
                "scada.batch-too-large",
                $"A maximum of {MaximumBatchTagCount} SCADA tags can be read in one request.");
        }

        if (request.TagNames.Any(string.IsNullOrWhiteSpace))
            return BatchError("scada.batch-tag-empty", "SCADA tag names cannot be empty.");

        return Results.Ok(await gateway.ReadTagsAsync(request.TagNames, ct));
    }

    private static async Task<IResult> WriteTagAsync(ScadaTagWriteRequest request, IPlantScadaGateway gateway, CancellationToken ct)
    {
        var result = await gateway.WriteTagAsync(request, ct);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    private static IResult BatchError(string code, string message)
    {
        return Results.BadRequest(new ScadaTagBatchReadErrorResponse
        {
            ErrorCode = code,
            ErrorMessage = message
        });
    }
}