using TechMES.Application.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// API просмотра рецептов.
/// </summary>
public static class RecipeEndpoints
{
    /// <summary>
    /// Регистрирует каталог и чтение одного рецепта.
    /// </summary>
    public static IEndpointRouteBuilder MapRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recipes/catalog", GetCatalogAsync);
        app.MapGet("/api/recipes/{recipeId:int}", GetRecipeAsync);

        return app;
    }

    /// <summary>
    /// Читает привязки объектов и заголовки рецептов.
    /// </summary>
    private static async Task<IResult> GetCatalogAsync(IRecipeStore store, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await store.GetCatalogAsync(ct));
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

    /// <summary>
    /// Читает стадии и параметры выбранного рецепта.
    /// </summary>
    private static async Task<IResult> GetRecipeAsync(int recipeId, IRecipeStore store, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (recipeId <= 0)
            return Results.Problem("Invalid recipe ID.", statusCode: 400);

        try
        {
            var result = await store.GetAsync(recipeId, ct);

            return result is null ? Results.Problem("Recipe no longer exists. Refresh the recipe list.", statusCode: 404) : Results.Ok(result);
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

    /// <summary>
    /// Подробности ошибки остаются в Runtime-логе.
    /// WEB получает понятное сообщение без строки подключения.
    /// </summary>
    private static IResult LoadFailure(ILoggerFactory loggerFactory, Exception exception)
    {
        loggerFactory.CreateLogger("RecipeEndpoints").LogError(exception, "Failed to read the recipe database.");

        return Results.Problem("Recipes could not be loaded. Check Recipes:ConnectionString and the Runtime log.", statusCode: 503);
    }
}