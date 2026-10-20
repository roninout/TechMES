using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>Каталог, предварительный просмотр и подтверждение — отдельные запросы.</summary>
    private static void MapBaseParameterEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recipes/base-parameters", GetBaseParametersAsync);
        app.MapPost("/api/recipes/base-parameters/preview", (RecipeBaseParameterBatchRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory logger, CancellationToken ct) => ProcessBaseParametersAsync(false, request, store, options, logger, ct));
        app.MapPost("/api/recipes/base-parameters/save", (RecipeBaseParameterBatchRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory logger, CancellationToken ct) => ProcessBaseParametersAsync(true, request, store, options, logger, ct));
    }

    /// <summary>Читает справочник без изменения БД.</summary>
    private static async Task<IResult> GetBaseParametersAsync(IRecipeStore store, ILoggerFactory logger, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await store.GetBaseParametersAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return LoadFailure(logger, ex);
        }
    }

    /// <summary>Проверяет прежний профиль доступа и журналирует только подтверждённую запись.</summary>
    private static async Task<IResult> ProcessBaseParametersAsync(bool save, RecipeBaseParameterBatchRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory logger, CancellationToken ct)
    {
        var error = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (error is not null) return Results.Problem(error, statusCode: 403);

        try
        {
            if (!save) return Results.Ok(await store.PreviewBaseParametersAsync(request, ct));

            var result = await store.SaveBaseParametersAsync(request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
                return Results.Problem(result.Error ?? "Base parameter save failed.", statusCode: result.Status == RecipeChangeStatus.Conflict ? 409 : 400);

            logger.CreateLogger("RecipeEndpoints").LogInformation("Base recipe parameters saved. Changed={Changed}, Synchronized={Synchronized}, Actor={Actor}", result.ChangedParameters, result.SynchronizedValues, request.Actor);

            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return LoadFailure(logger, ex);
        }
    }
}