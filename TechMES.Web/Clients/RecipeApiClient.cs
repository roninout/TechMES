using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TechMES.Contracts.Recipes;

namespace TechMES.Web.Clients;

/// <summary>
/// WEB-клиент рецептов.
/// Использует существующий адрес RuntimeService.
/// </summary>
public sealed class RecipeApiClient
{
    private readonly IHttpClientFactory _clients;

    public RecipeApiClient(IHttpClientFactory clients)
    {
        _clients = clients;
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
    /// Передаёт сообщение Runtime в интерфейс и освобождает HTTP-ответ.
    /// </summary>
    private async Task<T> ReadAsync<T>(string url, CancellationToken ct) where T : class
    {
        var client = _clients.CreateClient("RuntimeService");
        using var response = await client.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            ProblemDetails? problem = null;

            try
            {
                problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: ct);
            }
            catch (JsonException)
            {
                // При ошибке прокси ответ может быть HTML вместо ProblemDetails.
            }

            throw new InvalidOperationException(problem?.Detail ?? $"Recipe request failed: HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Runtime returned an empty recipe response.");
    }
}