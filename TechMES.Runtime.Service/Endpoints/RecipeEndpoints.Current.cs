using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Application.Scada;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    /// <summary>Один gate с записью ячейки: загрузки рецепта и одиночные записи не перемешиваются.</summary>
    private static async Task<IResult> ActivateCurrentRecipeAsync(RecipeChangeRequest request, IRecipeStore store, IPlantScadaGateway gateway, IEquipmentParamProvider paramProvider, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!options.Value.Enabled || options.Value.DryRun)
            return Results.Problem("Recipe loading requires ParamWrites:Enabled = true and DryRun = false.", statusCode: 409);

        var logger = loggerFactory.CreateLogger("RecipeEndpoints");
        RecipePlcUpload? plan = null;

        await RecipePlcWriteGate.WaitAsync(ct);

        try
        {
            var result = await store.ActivateAsync(request, async (prepared, token) =>
            {
                plan = prepared;
                await prepared.ExecuteAsync(gateway, token);
            }, ct);

            if (result.Status != RecipeChangeStatus.Saved)
                return Results.Problem(result.Error ?? "Current recipe was not assigned.", statusCode: result.Status == RecipeChangeStatus.NotFound ? 404 : result.Status == RecipeChangeStatus.Conflict ? 409 : 400);

            // PLC уже проверен, Current зафиксирован. Ошибка аудита не отменяет загрузку.
            if (options.Value.AuditEnabled && plan is not null)
            {
                try
                {
                    var equipment = new EquipmentDto { Name = plan.Page.Page, Description = request.ExpectedSnapshot!.Recipe.Name };
                    var saved = await paramProvider.TrySaveOperatorActionAsync(equipment, "Set as current", request.ExpectedPages[0].RecipeId.ToString(), request.SourceRecipeId.ToString(), "Load recipe from database to PLC", request.Actor, ct);

                    if (!saved)
                        result.Warning = "Recipe loaded; operator audit failed.";
                }
                catch (Exception ex)
                {
                    result.Warning = "Recipe loaded; operator audit could not be confirmed.";
                    logger.LogWarning(ex, "Recipe activation audit failed. Recipe={RecipeId}.", request.SourceRecipeId);
                }
            }

            logger.LogInformation("Recipe loaded and assigned Current. Recipe={RecipeId}, Unit={PageId}, Actor={Actor}.", result.RecipeId, plan?.Page.Id, request.Actor);

            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Recipe activation cancelled. Recipe={RecipeId}, WriteAttempted={Attempted}.", request.SourceRecipeId, plan?.WriteAttempted == true);
            throw;
        }
        catch (Exception ex)
        {
            var attempted = plan?.WriteAttempted == true;

            logger.LogError(ex, "Recipe activation failed. Recipe={RecipeId}, WriteAttempted={Attempted}.", request.SourceRecipeId, attempted);

            var detail = ex is InvalidOperationException ? ex.Message : "Check the Runtime log for details.";

            return Results.Problem(attempted ? "Recipe loading did not complete or its outcome is uncertain. PLC may contain partially written data. Check PLC and refresh before retrying. " + detail : "Recipe was not loaded; no TagWrite was sent. " + detail, statusCode: attempted ? 502 : 409);
        }
        finally
        {
            RecipePlcWriteGate.Release();
        }
    }
}