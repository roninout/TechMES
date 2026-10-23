using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Application.Recipes;
using TechMES.Contracts.Recipes;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// API чтения рецептов и сохранения значений, стадий и базовых параметров.
/// </summary>
public static partial class RecipeEndpoints
{
    /// <summary>Регистрирует просмотр и изменение рецептов, стадий и параметров.</summary>
    public static IEndpointRouteBuilder MapRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recipes/catalog", GetCatalogAsync);
        app.MapGet("/api/recipes/{recipeId:int}", GetRecipeAsync);
        app.MapGet("/api/recipes/{recipeId:int}/plc", ReadRecipePlcAsync);
        app.MapPost("/api/recipes/{recipeId:int}/plc/parameter", WriteRecipePlcAsync);
        app.MapPost("/api/recipes/{recipeId:int}/parameter", SaveParameterAsync);
        app.MapPost("/api/recipes/{recipeId:int}/values", SaveRecipeValuesAsync);
        app.MapPost("/api/recipes/change", ChangeRecipeAsync);
        app.MapPost("/api/recipes/{recipeId:int}/stage/change", ChangeStageAsync);
        app.MapGet("/api/recipes/{recipeId:int}/stage/templates", GetStageTemplatesAsync);
        app.MapPost("/api/recipes/{recipeId:int}/stage/add", AddStageAsync);
        app.MapPost("/api/recipes/{recipeId:int}/editor", SaveRecipeEditorAsync);

        MapBaseParameterEndpoints(app);
        MapBaseStageEndpoints(app);

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
    /// Проверяет права и сохраняет одну ячейку.
    /// Вызовов CtApi здесь нет.
    /// </summary>
    private static async Task<IResult> SaveParameterAsync(int recipeId, RecipeParameterSaveRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var authorizationError = AuthorizeSave(request, options.Value.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        try
        {
            var result = await store.SaveParameterAsync(recipeId, request, ct);

            if (result.Status != RecipeParameterSaveStatus.Saved)
            {
                var statusCode = result.Status switch
                {
                    RecipeParameterSaveStatus.NotFound => 404,
                    RecipeParameterSaveStatus.Conflict => 409,
                    RecipeParameterSaveStatus.InvalidValue => 400,
                    _ => 500
                };

                return Results.Problem(result.Error ?? "Recipe value could not be saved.", statusCode: statusCode);
            }

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("Recipe parameter saved. Recipe={RecipeId}, Stage={StageId}, Parameter={ParameterId}, Actor={Actor}, Old={Old}, New={New}", recipeId, request.StageId, request.ParameterId, request.Actor, request.ExpectedValue, result.Value);

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

    /// <summary>
    /// Управляет сохранённым рецептом, не назначая его текущим и не обращаясь к PLC.
    /// </summary>
    private static async Task<IResult> ChangeRecipeAsync(RecipeChangeRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var authorizationError = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        try
        {
            var result = await store.ChangeAsync(request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
            {
                var statusCode = result.Status switch
                {
                    RecipeChangeStatus.NotFound => 404,
                    RecipeChangeStatus.Conflict => 409,
                    RecipeChangeStatus.Invalid => 400,
                    _ => 500
                };

                return Results.Problem(result.Error ?? "Recipe operation failed.", statusCode: statusCode);
            }

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("Recipe operation completed. Operation={Operation}, Source={Source}, Recipe={RecipeId}, Name={Name}, Actor={Actor}", request.Operation, request.SourceRecipeId, result.RecipeId, request.Name, request.Actor);

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

    /// <summary>
    /// Проверяет доступ и изменяет сохранённую стадию без обращения к PLC.
    /// </summary>
    private static async Task<IResult> ChangeStageAsync(int recipeId, RecipeStageChangeRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var authorizationError = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        try
        {
            var result = await store.ChangeStageAsync(recipeId, request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
            {
                var statusCode = result.Status switch
                {
                    RecipeChangeStatus.NotFound => 404,
                    RecipeChangeStatus.Conflict => 409,
                    RecipeChangeStatus.Invalid => 400,
                    _ => 500
                };

                return Results.Problem(result.Error ?? "Recipe stage operation failed.", statusCode: statusCode);
            }

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("Recipe stage changed. Recipe={RecipeId}, Stage={StageId}, Operation={Operation}, Auto={Auto}, Actor={Actor}", recipeId, request.StageId, request.Operation, request.Auto, request.Actor);

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

    /// <summary>
    /// Возвращает шаблоны только семейства выбранного рецепта.
    /// </summary>
    private static async Task<IResult> GetStageTemplatesAsync(int recipeId, IRecipeStore store, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (recipeId <= 0)
            return Results.Problem("Invalid recipe ID.", statusCode: 400);

        try
        {
            var result = await store.GetStageTemplatesAsync(recipeId, ct);

            return result is null ? Results.Problem("The recipe no longer exists. Press Refresh.", statusCode: 404) : Results.Ok(result);
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

    /// <summary>Добавляет стадию и записывает фактически назначенный prefix в Runtime-лог.</summary>
    private static async Task<IResult> AddStageAsync(int recipeId, RecipeStageAddRequest request, IRecipeStore store, IOptions<ParamWriteOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var authorizationError = AuthorizeRecipeWrite(request.Actor, request.ActorGroups, options.Value.Authorization);

        if (authorizationError is not null)
            return Results.Problem(authorizationError, statusCode: 403);

        try
        {
            var result = await store.AddStageAsync(recipeId, request, ct);

            if (result.Status != RecipeChangeStatus.Saved)
            {
                var statusCode = result.Status switch
                {
                    RecipeChangeStatus.NotFound => 404,
                    RecipeChangeStatus.Conflict => 409,
                    RecipeChangeStatus.Invalid => 400,
                    _ => 500
                };

                return Results.Problem(result.Error ?? "The stage could not be added.", statusCode: statusCode);
            }

            loggerFactory.CreateLogger("RecipeEndpoints").LogInformation("Recipe stage added. Recipe={RecipeId}, Stage={StageId}, Template={TemplateId}, Prefix={Prefix}, Auto={Auto}, Actor={Actor}", recipeId, result.SelectedStageId, request.BaseStageId, result.TagPrefix, request.Auto, request.Actor);

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

    /// <summary>
    /// Сохраняет прежнюю точку проверки доступа при редактировании ячейки.
    /// </summary>
    private static string? AuthorizeSave(RecipeParameterSaveRequest request, ParamWriteAuthorizationOptions authorization)
    {
        return AuthorizeRecipeWrite(request.Actor, request.ActorGroups, authorization);
    }

    /// <summary>
    /// Один профиль доступа для значений, создания, копирования и переименования.
    /// </summary>
    private static string? AuthorizeRecipeWrite(string? actor, IReadOnlyCollection<string>? actorGroups, ParamWriteAuthorizationOptions authorization)
    {
        if (!authorization.Enabled)
            return null;

        if (authorization.RequireWindowsUser && string.IsNullOrWhiteSpace(actor))
            return "Recipe editing is denied: Windows user was not provided.";

        var users = authorization.AllowedUsers.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = authorization.AllowedGroups.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (users.Any(user => PrincipalMatches(actor, user)))
            return null;

        if ((actorGroups ?? []).Any(actual => groups.Any(group => PrincipalMatches(actual, group))))
            return null;

        return "Recipe editing is denied for this Windows user.";
    }

    /// <summary>
    /// Сравнивает полное DOMAIN\Name или короткое имя, как существующий Param write-flow.
    /// </summary>
    private static bool PrincipalMatches(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        actual = actual.Trim();

        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) || actual.Split('\\').Last().Equals(expected.Split('\\').Last(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Подробности ошибки остаются в Runtime-логе.
    /// WEB получает сообщение без строки подключения.
    /// </summary>
    private static IResult LoadFailure(ILoggerFactory loggerFactory, Exception exception)
    {
        loggerFactory.CreateLogger("RecipeEndpoints").LogError(exception, "Recipe database request failed.");

        return Results.Problem("Recipe database request failed. Check Recipes:ConnectionString and the Runtime log.", statusCode: 503);
    }
}