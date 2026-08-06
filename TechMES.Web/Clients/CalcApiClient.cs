using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TechMES.Contracts.Calc;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для Calc API.
///
/// WEB работает только с TechMES.Contracts.
/// Формулы, проверка заданий и PostgreSQL остаются в Runtime.Service.
/// </summary>
public sealed class CalcApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Возвращает все алгоритмы, встроенные в текущую версию Runtime.
    /// </summary>
    public async Task<IReadOnlyList<CalcDefinitionDto>> GetDefinitionsAsync(CancellationToken ct = default)
    {
        var response = await SendAsync<CalcDefinitionsResponse>(HttpMethod.Get, "api/calc/definitions", null, ct);
        return response.Items;
    }

    /// <summary>
    /// Выполняет ручной расчёт без PostgreSQL, CtApi и Calc.Service.
    /// </summary>
    public async Task<CalcTestResponse> TestAsync(string definitionCode, IReadOnlyDictionary<string, object?> parameters, bool includeTrace, CancellationToken ct = default)
    {
        var jsonParameters = parameters
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Key,
                item => JsonSerializer.SerializeToElement(item.Value, item.Value!.GetType()),
                StringComparer.OrdinalIgnoreCase);

        var request = new CalcTestRequest
        {
            DefinitionCode = definitionCode,
            Parameters = jsonParameters,
            IncludeTrace = includeTrace
        };

        return await SendAsync<CalcTestResponse>(HttpMethod.Post, "api/calc/test", request, ct);
    }

    /// <summary>
    /// Возвращает все сохранённые расчётные задания.
    /// </summary>
    public async Task<IReadOnlyList<CalcJobDto>> GetJobsAsync(CancellationToken ct = default)
    {
        var response = await SendAsync<CalcJobsResponse>(HttpMethod.Get, "api/calc/jobs", null, ct);
        return response.Items;
    }

    /// <summary>
    /// Возвращает текущие состояния всех расчётных заданий.
    /// </summary>
    public async Task<IReadOnlyList<CalcJobStateDto>> GetStatesAsync(CancellationToken ct = default)
    {
        var response = await SendAsync<CalcJobStatesResponse>(HttpMethod.Get, "api/calc/states", null, ct);
        return response.Items;
    }

    /// <summary>
    /// Возвращает текущее состояние одного задания.
    /// </summary>
    public Task<CalcJobStateDto> GetJobStateAsync(long jobId, CancellationToken ct = default)
    {
        return SendAsync<CalcJobStateDto>(HttpMethod.Get, $"api/calc/jobs/{jobId}/state", null, ct);
    }

    /// <summary>
    /// Возвращает одно расчётное задание.
    /// </summary>
    public Task<CalcJobDto> GetJobAsync(long id, CancellationToken ct = default)
    {
        return SendAsync<CalcJobDto>(HttpMethod.Get, $"api/calc/jobs/{id}", null, ct);
    }

    /// <summary>
    /// Создаёт новое задание.
    /// </summary>
    public Task<CalcJobDto> CreateJobAsync(CalcJobSaveRequest request, CancellationToken ct = default)
    {
        return SendAsync<CalcJobDto>(HttpMethod.Post, "api/calc/jobs", request, ct);
    }

    /// <summary>
    /// Обновляет задание с проверкой ExpectedRevision.
    /// </summary>
    public Task<CalcJobDto> UpdateJobAsync(long id, CalcJobSaveRequest request, CancellationToken ct = default)
    {
        return SendAsync<CalcJobDto>(HttpMethod.Put, $"api/calc/jobs/{id}", request, ct);
    }

    /// <summary>
    /// Удаляет задание.
    /// </summary>
    public async Task DeleteJobAsync(long id, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/calc/jobs/{id}");
        using var response = await CreateClient().SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, ct);
    }

    /// <summary>
    /// Выполняет HTTP-запрос и преобразует успешный JSON-ответ.
    /// </summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);

        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await CreateClient().SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Runtime returned an empty Calc API response.");
    }

    /// <summary>
    /// Преобразует Calc API error DTO в типизированное WEB-исключение.
    /// </summary>
    private static async Task<CalcApiClientException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = TryDeserialize<CalcJobRevisionConflictResponse>(content);

            if (conflict is not null && conflict.JobId > 0)
            {
                return new CalcApiClientException(response.StatusCode, conflict.ErrorCode, conflict.ErrorMessage,
                    conflict.JobId, conflict.ExpectedRevision, conflict.CurrentRevision);
            }
        }

        var error = TryDeserialize<CalcApiErrorResponse>(content);
        var message = error?.ErrorMessage;

        if (string.IsNullOrWhiteSpace(message))
            message = response.ReasonPhrase ?? $"Calc API returned HTTP {(int)response.StatusCode}.";

        return new CalcApiClientException(response.StatusCode, error?.ErrorCode, message);
    }

    /// <summary>
    /// Безопасно разбирает error body, поскольку proxy или сервер
    /// могут вернуть ответ, не соответствующий Calc DTO.
    /// </summary>
    private static T? TryDeserialize<T>(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private HttpClient CreateClient()
    {
        return httpClientFactory.CreateClient("RuntimeService");
    }
}