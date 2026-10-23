using System.Globalization;
using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Application.Scada;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Contracts.Recipes;
using TechMES.Contracts.Scada;

namespace TechMES.Runtime.Service.Endpoints;

public static partial class RecipeEndpoints
{
    // Проверка текущего значения и запись двух запросов Recipes не должны перемешиваться.
    private static readonly SemaphoreSlim RecipePlcWriteGate = new(1, 1);

    /// <summary>Записывает один параметр по его адресу; код стадии _NUM здесь не читается и не изменяется.</summary>
    private static async Task<IResult> WriteRecipePlcAsync(int recipeId, RecipePlcWriteRequest request, IRecipeStore store, IPlantScadaGateway gateway, IEquipmentParamProvider paramProvider, IOptions<ParamWriteOptions> writeOptions, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (recipeId <= 0 || request.Write is null || request.PageId <= 0 || request.StageId <= 0 || request.ColumnId <= 0 || string.IsNullOrWhiteSpace(request.LayoutStamp) || !double.IsFinite(request.ExpectedValue))
            return Results.Problem("Invalid recipe PLC write request.", statusCode: 400);

        var options = writeOptions.Value;
        var authorizationError = AuthorizeRecipeWrite(request.Write.Actor, request.Write.ActorGroups, options.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        var result = new ParamWriteResponse { Actor = request.Write.Actor, Time = DateTime.Now };

        if (!options.Enabled)
            return RejectRecipePlcWrite(result, "Param writes are disabled. Set ParamWrites:Enabled = true.");

        if (options.RequireComment && string.IsNullOrWhiteSpace(request.Write.Comment))
            return RejectRecipePlcWrite(result, "Comment is required for PLC write.");

        await RecipePlcWriteGate.WaitAsync(ct);
        var writeAttempted = false;

        try
        {
            var catalog = await store.GetCatalogAsync(ct);
            var page = catalog.Pages.FirstOrDefault(item => item.Id == request.PageId);
            var snapshot = await store.GetAsync(recipeId, ct);

            if (page is null || snapshot is null)
                return RejectRecipePlcWrite(result, "The Unit or recipe no longer exists. Press Refresh.");

            if (!RecipePlcLayout.GetStamp(snapshot).Equals(request.LayoutStamp, StringComparison.Ordinal))
                return RejectRecipePlcWrite(result, "The recipe structure has changed. Press Refresh.");

            var stage = snapshot.Stages.FirstOrDefault(item => item.Id == request.StageId);
            var column = snapshot.Columns.FirstOrDefault(item => item.Id == request.ColumnId);

            if (stage is null || column is null || !stage.Parameters.ContainsKey(column.Id))
                return RejectRecipePlcWrite(result, "The parameter does not belong to this stage.");

            if (stage.Number <= 0 || string.IsNullOrWhiteSpace(stage.TagPrefix) || string.IsNullOrWhiteSpace(column.TagSuffix) || column.Kind is not (RecipeParameterKind.InputField or RecipeParameterKind.CheckBox))
                return RejectRecipePlcWrite(result, "The stage PLC address or parameter type is invalid.");

            // Суффикс используем буквально из БД, как в работающем чтении.
            var prefix = stage.TagPrefix.Trim();
            var tag = RecipeParameterTag(stage, column);

            result.EquipmentName = page.Page;
            result.ItemName = column.Name;
            result.TagName = tag;

            if (column.TagSuffix.Trim().Equals("NUM", StringComparison.OrdinalIgnoreCase))
                return RejectRecipePlcWrite(result, "NUM is a service tag and cannot be edited as a parameter.");

            var columns = snapshot.Columns.ToDictionary(item => item.Id);
            var addressCount = snapshot.Stages.Sum(item => item.Parameters.Keys.Count(id => columns.TryGetValue(id, out var definition) && RecipeParameterTag(item, definition).Equals(tag, StringComparison.OrdinalIgnoreCase)));

            if (addressCount != 1)
                return RejectRecipePlcWrite(result, "More than one recipe cell uses this PLC address.");

            var templates = await store.GetStageTemplatesAsync(recipeId, ct);

            if (templates is null || templates.BaseRecipeId != page.BaseRecipeId)
                return RejectRecipePlcWrite(result, "The recipe belongs to another Unit family.");

            var current = page.RecipeId == recipeId ? snapshot : await store.GetAsync(page.RecipeId, ct);

            if (current is null || !current.Stages.Any(item => item.TagPrefix.Trim().Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                return RejectRecipePlcWrite(result, "The PLC prefix does not belong to the selected Unit's current recipe.");

            if (!RecipePlcReader.TryReadValue(request.Write.Value, column.Kind, out var newValue))
                return RejectRecipePlcWrite(result, "Enter a finite number or a Boolean value 0/1.");

            result.WrittenValue = newValue.ToString("G17", CultureInfo.InvariantCulture);

            // Как в исходном приложении, отдельная ячейка работает только со своим тегом.
            // _NUM записывается кодом шаблона при загрузке рецепта целиком.
            var readings = await ReadRecipeWriteValuesAsync(gateway, tag, ct);

            if (!TryRecipeWriteReading(readings, tag, column.Kind, out var currentValue))
                return RejectRecipePlcWrite(result, $"Cannot read the current PLC value: {tag}.");

            result.CurrentValue = currentValue.ToString("G17", CultureInfo.InvariantCulture);

            // Значение из диалога должно всё ещё соответствовать фактическому значению PLC.
            if (currentValue != request.ExpectedValue)
                return RejectRecipePlcWrite(result, "The PLC value changed while the dialog was open. Read the current value and try again.");

            if (options.DryRun)
            {
                result.Success = true;
                result.DryRun = true;
                result.Message = "Dry-run: PLC address and current value validated. No write was sent.";
                return Results.Ok(result);
            }

            ct.ThrowIfCancellationRequested();
            writeAttempted = true;

            var written = await gateway.WriteTagAsync(new ScadaTagWriteRequest { TagName = tag, Value = result.WrittenValue }, ct);

            if (!written.Success)
                return RejectRecipePlcWrite(result, "PLC write was not confirmed: " + written.Error);

            // Используем тот же SaveActionOperators, что и Equipment -> Param.
            if (options.AuditEnabled)
            {
                result.AuditAttempted = true;

                // SaveActionOperators добавляет sWndTitle в квадратных скобках после описания.
                var auditDescription = $"stage { stage.Number} : {column.Name}";
                var equipment = new EquipmentDto { Name = tag, Description = $"{snapshot.Recipe.Name.Trim()}" };

                result.AuditSucceeded = await paramProvider.TrySaveOperatorActionAsync(equipment, $"{stage.Number}_{column.TagSuffix.Trim()}", result.CurrentValue, result.WrittenValue, auditDescription, request.Write.Actor, ct);
            }

            var verified = false;

            // Даём контроллеру время применить запись; повторного TagWrite здесь нет.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(200, ct);
                readings = await ReadRecipeWriteValuesAsync(gateway, tag, ct);

                if (TryRecipeWriteReading(readings, tag, column.Kind, out var actual) && SameRecipePlcValue(actual, newValue))
                {
                    verified = true;
                    break;
                }
            }

            if (!verified)
                return RejectRecipePlcWrite(result, "TagWrite was sent, but the PLC value could not be verified. Read current values before retrying.");

            result.Success = true;
            result.Message = result.AuditAttempted && !result.AuditSucceeded ? "PLC value written and verified; operator audit failed." : "PLC value written and verified.";

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("Recipe PLC value verified. Recipe={RecipeId}, Stage={StageId}, Parameter={ColumnId}, Tag={Tag}, Old={Old}, New={New}, Actor={Actor}.", recipeId, stage.Id, column.Id, tag, result.CurrentValue, result.WrittenValue, request.Write.Actor);

            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("RecipeEndpoints").LogError(ex, "Recipe PLC write failed. Recipe={RecipeId}, Stage={StageId}, Parameter={ColumnId}, Attempted={Attempted}.", recipeId, request.StageId, request.ColumnId, writeAttempted);

            return RejectRecipePlcWrite(result, writeAttempted ? "The PLC write outcome could not be confirmed. Read current values before retrying." : "Cannot prepare the PLC write. Check the Runtime log.");
        }
        finally
        {
            RecipePlcWriteGate.Release();
        }
    }

    private static string RecipeParameterTag(RecipeStageDto stage, RecipeColumnDto column) => $"{stage.TagPrefix.Trim()}_{stage.Number.ToString(CultureInfo.InvariantCulture)}_{column.TagSuffix.Trim()}";

    private static async Task<Dictionary<string, ScadaTagBatchReadItem>> ReadRecipeWriteValuesAsync(IPlantScadaGateway gateway, string tag, CancellationToken ct)
    {
        var batch = await gateway.ReadTagsAsync([tag], ct);
        return batch.Items.ToDictionary(item => item.TagName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryRecipeWriteReading(Dictionary<string, ScadaTagBatchReadItem> readings, string tag, RecipeParameterKind kind, out double value)
    {
        value = 0;

        return readings.TryGetValue(tag, out var item) && item.Success && item.Quality is not (ScadaTagQuality.Bad or ScadaTagQuality.Uncertain) && RecipePlcReader.TryReadValue(item.Value, kind, out value);
    }

    private static bool SameRecipePlcValue(double actual, double expected) => actual == expected || Math.Abs(actual - expected) <= Math.Max(1e-9, Math.Abs(expected) * 1e-7);

    private static IResult RejectRecipePlcWrite(ParamWriteResponse result, string error)
    {
        result.Success = false;
        result.Error = error;

        return Results.Ok(result);
    }
}