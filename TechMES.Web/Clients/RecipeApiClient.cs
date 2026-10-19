using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TechMES.Contracts.Param;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Clients;

/// <summary>
/// Каталог, снимки и сохранение значений рецептов через Runtime.
/// </summary>
public sealed class RecipeApiClient
{
    private readonly IHttpClientFactory _clients;
    private readonly ParamApiClient _paramApi;

    public RecipeApiClient(IHttpClientFactory clients, ParamApiClient paramApi)
    {
        _clients = clients;
        _paramApi = paramApi;
    }

    /// <summary>
    /// Получает каталог без стадий всех рецептов.
    /// </summary>
    public Task<RecipeCatalogDto> GetCatalogAsync(CancellationToken ct = default) => ReadAsync<RecipeCatalogDto>("api/recipes/catalog", ct);

    /// <summary>
    /// Получает снимок выбранного рецепта.
    /// </summary>
    public Task<RecipeSnapshotDto> GetAsync(int recipeId, CancellationToken ct = default) => ReadAsync<RecipeSnapshotDto>($"api/recipes/{recipeId}", ct);

    /// <summary>
    /// Добавляет существующую Windows identity и сохраняет одну ячейку в БД.
    /// </summary>
    public async Task<RecipeParameterSaveResult> SaveParameterAsync(int recipeId, RecipeParameterSaveRequest request, CancellationToken ct = default)
    {
        var actor = new ParamWriteRequest();

        await _paramApi.FillWriteActorAsync(actor);
        ct.ThrowIfCancellationRequested();

        request.Actor = actor.Actor;
        request.ActorGroups = actor.ActorGroups;

        var client = _clients.CreateClient("RuntimeService");

        using var response = await client.PostAsJsonAsync($"api/recipes/{recipeId}/parameter", request, ct);

        return await ReadResponseAsync<RecipeParameterSaveResult>(response, ct);
    }

    /// <summary>
    /// Создаёт, копирует или переименовывает рецепт с существующей Windows identity.
    /// </summary>
    public async Task<RecipeChangeResult> ChangeAsync(RecipeChangeRequest request, CancellationToken ct = default)
    {
        var actor = new ParamWriteRequest();

        await _paramApi.FillWriteActorAsync(actor);
        ct.ThrowIfCancellationRequested();

        request.Actor = actor.Actor;
        request.ActorGroups = actor.ActorGroups;

        var client = _clients.CreateClient("RuntimeService");

        using var response = await client.PostAsJsonAsync("api/recipes/change", request, ct);

        return await ReadResponseAsync<RecipeChangeResult>(response, ct);
    }

    /// <summary>
    /// Изменяет стадию с тем же профилем доступа, что и остальные операции рецептов.
    /// </summary>
    public async Task<RecipeStageChangeResult> ChangeStageAsync(int recipeId, RecipeStageChangeRequest request, CancellationToken ct = default)
    {
        var actor = new ParamWriteRequest();

        await _paramApi.FillWriteActorAsync(actor);
        ct.ThrowIfCancellationRequested();

        request.Actor = actor.Actor;
        request.ActorGroups = actor.ActorGroups;

        var client = _clients.CreateClient("RuntimeService");

        using var response = await client.PostAsJsonAsync($"api/recipes/{recipeId}/stage/change", request, ct);

        return await ReadResponseAsync<RecipeStageChangeResult>(response, ct);
    }

    /// <summary>
    /// Читает доступные шаблоны перед открытием диалога добавления стадии.
    /// </summary>
    public Task<RecipeStageTemplateCatalogDto> GetStageTemplatesAsync(int recipeId, CancellationToken ct = default) => ReadAsync<RecipeStageTemplateCatalogDto>($"api/recipes/{recipeId}/stage/templates", ct);

    /// <summary>
    /// Добавляет стадию, передавая существующую Windows identity.
    /// </summary>
    public async Task<RecipeStageChangeResult> AddStageAsync(int recipeId, RecipeStageAddRequest request, CancellationToken ct = default)
    {
        var actor = new ParamWriteRequest();

        await _paramApi.FillWriteActorAsync(actor);
        ct.ThrowIfCancellationRequested();

        request.Actor = actor.Actor;
        request.ActorGroups = actor.ActorGroups;

        var client = _clients.CreateClient("RuntimeService");

        using var response = await client.PostAsJsonAsync($"api/recipes/{recipeId}/stage/add", request, ct);

        return await ReadResponseAsync<RecipeStageChangeResult>(response, ct);
    }

    /// <summary>
    /// Выполняет GET и освобождает HTTP-ответ.
    /// </summary>
    private async Task<T> ReadAsync<T>(string url, CancellationToken ct) where T : class
    {
        var client = _clients.CreateClient("RuntimeService");

        using var response = await client.GetAsync(url, ct);

        return await ReadResponseAsync<T>(response, ct);
    }

    /// <summary>
    /// Сохраняет HTTP-статус ошибки, чтобы WEB отличал конфликт от недоступности Runtime.
    /// </summary>
    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            ProblemDetails? problem = null;

            try
            {
                problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: ct);
            }
            catch (JsonException)
            {
                // Прокси может вернуть HTML вместо ProblemDetails.
            }

            throw new HttpRequestException(problem?.Detail ?? $"Recipe request failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Runtime returned an empty recipe response.");
    }
}