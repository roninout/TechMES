using System.Net.Http.Json;
using System.Text.Json;
using TechMES.Contracts.Calc;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для каталога расчётов и ручного тестирования.
///
/// WEB не ссылается на TechMES.Calc и работает только с транспортными DTO.
/// Все формулы и валидация выполняются внутри Runtime.Service.
/// </summary>
public sealed class CalcApiClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>
    /// Возвращает все алгоритмы, встроенные в текущую версию Runtime.
    /// </summary>
    public async Task<IReadOnlyList<CalcDefinitionDto>> GetDefinitionsAsync(CancellationToken ct = default)
    {
        var response = await CreateClient().GetFromJsonAsync<CalcDefinitionsResponse>("api/calc/definitions", ct);
        return response?.Items ?? [];
    }

    /// <summary>
    /// Выполняет один ручной расчёт без CtApi, PostgreSQL и Calc.Service.
    /// </summary>
    public async Task<CalcTestResponse> TestAsync(string definitionCode, IReadOnlyDictionary<string, object?> parameters,
        bool includeTrace, CancellationToken ct = default)
    {
        var jsonParameters = parameters
            .Where(item => item.Value is not null)
            .ToDictionary(
                item => item.Key,
                item => JsonSerializer.SerializeToElement(item.Value, item.Value!.GetType()),
                StringComparer.OrdinalIgnoreCase);

        var request = new CalcTestRequest
        {
            DefinitionCode = definitionCode,
            Parameters = jsonParameters,
            IncludeTrace = includeTrace
        };

        using var response = await CreateClient().PostAsJsonAsync("api/calc/test", request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<CalcTestResponse>(cancellationToken: ct)
               ?? throw new InvalidOperationException("Runtime returned an empty calculation response.");
    }

    /// <summary>
    /// Возвращает именованный HttpClient, настроенный на Runtime.Service.
    /// </summary>
    private HttpClient CreateClient()
    {
        return httpClientFactory.CreateClient("RuntimeService");
    }
}