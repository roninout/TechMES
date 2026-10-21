using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>Проверяет существующие права записи и сохраняет черновик целиком.</summary>
    private static async Task<IResult> SaveRecipeEditorAsync(int recipeId, RecipeEditorSaveRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var authorizationError = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        try
        {
            var result = await store.SaveEditorAsync(recipeId, request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
                return Results.Problem(result.Error ?? "Recipe save failed.", statusCode: result.Status switch { RecipeChangeStatus.NotFound => 404, RecipeChangeStatus.Conflict => 409, RecipeChangeStatus.Invalid => 400, _ => 500 });

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("Recipe draft saved. Recipe={RecipeId}, Stages={Count}, Actor={Actor}", recipeId, result.Snapshot?.Stages.Count, request.Actor);

            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LoadFailure(loggerFactory, ex);
        }
    }
}