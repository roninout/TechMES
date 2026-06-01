using System.Net.Http.Json;
using TechMES.Contracts.Param;

namespace TechMES.Web.Clients;

/// <summary>
/// HTTP-клиент WEB-слоя для Param API Runtime.Service.
/// Blazor-компоненты не формируют URL напрямую, а вызывают этот класс.
/// </summary>
public sealed class ParamApiClient
{
    /// <summary>
    /// Фабрика именованного клиента RuntimeService, настроенного в Program.cs WEB-проекта.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Создает клиент Param API. Сам HttpClient живет в DI и переиспользуется фабрикой.
    /// </summary>
    public ParamApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Читает текущий snapshot Param для выбранного оборудования.
    /// Возвращает unsupported-response вместо null, чтобы UI не падал на пустом ответе.
    /// </summary>
    public async Task<ParamSnapshotResponse> GetSnapshotAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamSnapshotResponse>(
            $"api/param/{encodedName}/snapshot",
            ct);

        return result ?? new ParamSnapshotResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty Param snapshot."
        };
    }

    /// <summary>
    /// Читает trend-точки для графика Param.
    /// fromUtc/toUtc передаются только при просмотре истории; live-режим работает через windowMinutes.
    /// </summary>
    public async Task<ParamTrendResponse> GetTrendAsync(
        string equipmentName,
        int windowMinutes,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);
        var query = $"windowMinutes={windowMinutes}";

        if (fromUtc.HasValue)
            query += $"&fromUtc={Uri.EscapeDataString(ToQueryUtc(fromUtc.Value))}";

        if (toUtc.HasValue)
            query += $"&toUtc={Uri.EscapeDataString(ToQueryUtc(toUtc.Value))}";

        var result = await client.GetFromJsonAsync<ParamTrendResponse>(
            $"api/param/{encodedName}/trend?{query}",
            ct);

        return result ?? new ParamTrendResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty Param trend."
        };
    }

    /// <summary>
    /// Читает PLC reference page.
    /// </summary>
    public async Task<ParamPlcRefsResponse> GetPlcRefsAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamPlcRefsResponse>(
            $"api/param/{encodedName}/refs/plc",
            ct);

        return result ?? new ParamPlcRefsResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty PLC references."
        };
    }

    /// <summary>
    /// Читает DI/DO reference page.
    /// </summary>
    public async Task<ParamDiDoRefsResponse> GetDiDoRefsAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamDiDoRefsResponse>(
            $"api/param/{encodedName}/refs/dido",
            ct);

        return result ?? new ParamDiDoRefsResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty DI/DO references."
        };
    }

    /// <summary>
    /// Читает DryRun reference page.
    /// </summary>
    public async Task<ParamDryRunResponse> GetDryRunAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamDryRunResponse>(
            $"api/param/{encodedName}/refs/dryrun",
            ct);

        return result ?? new ParamDryRunResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty DryRun data."
        };
    }

    /// <summary>
    /// Читает ATV reference page.
    /// </summary>
    public async Task<ParamAtvRefResponse> GetAtvRefAsync(
        string equipmentName,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamAtvRefResponse>(
            $"api/param/{encodedName}/refs/atv",
            ct);

        return result ?? new ParamAtvRefResponse
        {
            EquipmentName = equipmentName,
            Supported = false,
            Message = "Runtime Service returned empty ATV data."
        };
    }

    /// <summary>
    /// Отправляет запрос на запись Param item.
    /// Даже BadRequest пытаемся разобрать как ParamWriteResponse, потому что backend
    /// кладет туда человекочитаемую причину отказа.
    /// </summary>
    public async Task<ParamWriteResponse> WriteAsync(
        string equipmentName,
        ParamWriteRequest request,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        using var response = await client.PostAsJsonAsync(
            $"api/param/{encodedName}/write",
            request,
            ct);

        var result = await response.Content.ReadFromJsonAsync<ParamWriteResponse>(cancellationToken: ct);
        if (result is not null)
            return result;

        return new ParamWriteResponse
        {
            EquipmentName = equipmentName,
            ItemName = request.ItemName,
            Success = false,
            Error = response.IsSuccessStatusCode
                ? "Runtime Service returned empty Param write response."
                : $"Runtime Service rejected Param write: {(int)response.StatusCode} {response.ReasonPhrase}"
        };
    }

    /// <summary>
    /// Возвращает именованный RuntimeService HttpClient.
    /// </summary>
    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("RuntimeService");
    }

    /// <summary>
    /// Форматирует время в ISO UTC для query string trend endpoint.
    /// </summary>
    private static string ToQueryUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

        return utc.ToString("O");
    }
}
