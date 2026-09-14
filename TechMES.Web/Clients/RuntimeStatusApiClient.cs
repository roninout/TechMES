using System.Net.Http.Json;
using TechMES.Contracts.Runtime;
using TechMES.Contracts.Scada;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент для получения состояния Runtime.Service.
///
/// WEB не проверяет PostgreSQL напрямую.
/// Он спрашивает Runtime.Service через /api/health.
/// </summary>
public sealed class RuntimeStatusApiClient
{
    /// <summary>
    /// Фабрика именованного клиента RuntimeService.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Создает клиент диагностики Runtime.Service.
    /// </summary>
    public RuntimeStatusApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Возвращает HttpClient, настроенный на Runtime.Service.
    /// </summary>
    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    /// <summary>
    /// Читает /api/health. Даже при 503 пытается разобрать DTO,
    /// потому что backend возвращает полезное описание ошибки.
    /// </summary>
    public async Task<RuntimeHealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        var response = await client.GetAsync("api/health", ct);

        var health = await response.Content.ReadFromJsonAsync<RuntimeHealthResponse>(
            cancellationToken: ct);

        if (health is null)
        {
            return new RuntimeHealthResponse
            {
                Status = "ERROR",
                Database = "Unknown",
                Error = "Runtime Service returned empty health response.",
                Time = DateTime.Now
            };
        }

        return health;
    }

    public async Task<PlantScadaHealthResponse> GetScadaHealthAsync(CancellationToken ct = default)
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(
            "api/scada/health",
            ct);

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<PlantScadaHealthResponse>(
                cancellationToken: ct)
            ?? throw new InvalidOperationException(
                "Runtime returned an empty SCADA health response.");
    }
}
