using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>Чтение, анализ и подтверждённая запись шаблона.</summary>
    private static void MapBaseStageEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recipes/base-stages/{familyId:int}", GetBaseStagesAsync);
        app.MapPost("/api/recipes/base-stages/preview", (RecipeBaseStageRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory logger, CancellationToken ct) => ProcessBaseStageAsync(false, request, store, options, logger, ct));
        app.MapPost("/api/recipes/base-stages/save", (RecipeBaseStageRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory logger, CancellationToken ct) => ProcessBaseStageAsync(true, request, store, options, logger, ct));
    }

    /// <summary>Получает состав и позиции карточек выбранного семейства.</summary>
    private static async Task<IResult> GetBaseStagesAsync(int familyId, IRecipeStore store, ILoggerFactory logger, CancellationToken ct)
    {
        if (familyId <= 0) return Results.Problem("Invalid recipe family.", statusCode: 400);

        try
        {
            return Results.Ok(await store.GetBaseStagesAsync(familyId, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return LoadFailure(logger, ex);
        }
    }

    /// <summary>Использует существующий профиль доступа к изменению рецептов.</summary>
    private static async Task<IResult> ProcessBaseStageAsync(bool save, RecipeBaseStageRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory logger, CancellationToken ct)
    {
        var error = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (error is not null) return Results.Problem(error, statusCode: 403);

        try
        {
            if (!save) return Results.Ok(await store.PreviewBaseStageAsync(request, ct));

            var result = await store.SaveBaseStageAsync(request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
                return Results.Problem(result.Error ?? "Stage save failed.", statusCode: result.Status == RecipeChangeStatus.Conflict ? 409 : 400);

            logger.CreateLogger("RecipeEndpoints").LogInformation("Base stage saved and synchronized. Stage={Stage}, Actor={Actor}", result.StageId, request.Actor);

            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return LoadFailure(logger, ex);
        }
    }
}