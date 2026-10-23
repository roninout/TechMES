using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>Сохраняет подтверждённые WEB значения в БД, без повторного чтения или записи PLC.</summary>
    private static async Task<IResult> SaveRecipeValuesAsync(int recipeId, RecipeValuesSaveRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var authorizationError = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        try
        {
            var result = await store.SaveValuesAsync(recipeId, request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
                return Results.Problem(result.Error ?? "Recipe values could not be saved.", statusCode: result.Status switch { RecipeChangeStatus.NotFound => 404, RecipeChangeStatus.Conflict => 409, RecipeChangeStatus.Invalid => 400, _ => 500 });

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("PLC snapshot saved to recipe database. Recipe={RecipeId}, Values={Count}, Actor={Actor}", recipeId, request.Values.Count, request.Actor);

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