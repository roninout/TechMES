using TechMES.Application.Recipes;
using TechMES.Application.Scada;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>Тег mode берём из БД; WEB не может подменить адрес проверки режима.</summary>
    private static async Task<IResult?> CheckCurrentRecipeModeAsync(RecipeChangeRequest request, IRecipeStore store, IPlantScadaGateway gateway, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (request.ExpectedPages is not { Count: 1 } || request.ExpectedPages[0] is not { } expected)
            return Results.Problem("Select one Unit before assigning the Current recipe.", statusCode: 400);

        var catalog = await store.GetCatalogAsync(ct);
        var page = catalog.Pages.FirstOrDefault(item => item.Id == expected.Id);

        if (page is null)
            return Results.Problem("The selected Unit no longer exists. Press Refresh.", statusCode: 404);

        if (page.Page != expected.Page || page.RecipeId != expected.RecipeId || page.BaseRecipeId != expected.BaseRecipeId || page.CurrentStageTag != expected.CurrentStageTag || page.ModeTag != expected.ModeTag)
            return Results.Problem("The Unit assignment or control tags changed. Press Refresh before assigning.", statusCode: 409);

        try
        {
            var error = await RecipePlcReader.ReadManualModeErrorAsync(page, gateway, ct);

            return error is null ? null : Results.Problem(error, statusCode: 409);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("RecipeEndpoints").LogWarning(ex, "Cannot verify recipe manual mode. Unit={PageId}, Tag={Tag}.", page.Id, page.ModeTag);
            return Results.Problem("Manual mode could not be verified. Current recipe was not changed. Check the Runtime log.", statusCode: 409);
        }
    }
}