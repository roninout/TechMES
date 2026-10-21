using TechMES.Application.Recipes;
using TechMES.Application.Scada;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>WEB передаёт ID и контрольную сумму; адреса тегов Runtime берёт только из БД.</summary>
    private static async Task<IResult> ReadRecipePlcAsync(int recipeId, int pageId, string layoutStamp, IRecipeStore store, IPlantScadaGateway gateway, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (recipeId <= 0 || pageId <= 0 || string.IsNullOrWhiteSpace(layoutStamp))
            return Results.Problem("Select a Unit and its current recipe.", statusCode: 400);

        try
        {
            var catalog = await store.GetCatalogAsync(ct);
            var page = catalog.Pages.FirstOrDefault(item => item.Id == pageId);

            if (page is null || page.RecipeId != recipeId)
                return Results.Problem("The selected recipe is no longer current for this Unit. Press Refresh.", statusCode: 409);

            var snapshot = await store.GetAsync(recipeId, ct);

            if (snapshot is null)
                return Results.Problem("The recipe no longer exists. Press Refresh.", statusCode: 404);

            if (!RecipePlcLayout.GetStamp(snapshot).Equals(layoutStamp, StringComparison.Ordinal))
                return Results.Problem("The recipe structure has changed. Press Refresh before reading PLC values.", statusCode: 409);

            return Results.Ok(await RecipePlcReader.ReadAsync(snapshot, pageId, gateway, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("RecipeEndpoints").LogError(ex, "Recipe PLC read failed. Recipe={RecipeId}, Unit={PageId}.", recipeId, pageId);
            return Results.Problem("Cannot read recipe PLC values. Check the Runtime log.", statusCode: 503);
        }
    }
}