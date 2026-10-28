using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using TechMES.Contracts.Param;
using TechMES.Web.Security;

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
    /// Источник текущего Windows/Blazor пользователя.
    /// При включенном Negotiate здесь будет реальный пользователь браузера.
    /// </summary>
    private readonly AuthenticationStateProvider _authenticationStateProvider;

    /// <summary>
    /// Дополнительный доступ к HttpContext для ранних HTTP-запросов.
    /// В Blazor Server он не всегда доступен во время SignalR-событий, поэтому используется как fallback.
    /// </summary>
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Создает клиент Param API. Сам HttpClient живет в DI и переиспользуется фабрикой.
    /// </summary>
    public ParamApiClient(
        IHttpClientFactory httpClientFactory,
        AuthenticationStateProvider authenticationStateProvider,
        IHttpContextAccessor httpContextAccessor)
    {
        _httpClientFactory = httpClientFactory;
        _authenticationStateProvider = authenticationStateProvider;
        _httpContextAccessor = httpContextAccessor;
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
    /// Читает Runtime-данные вкладки PID Tune для VGA.
    ///
    /// tuneSettings используется как временный черновик после Check:
    /// PV/SP/Test Kp ещё не обязаны быть сохранены в БД,
    /// но Runtime уже может показать тренды и online Test Kp.
    /// </summary>
    public async Task<ParamTuneRuntimeResponse> GetTuneAsync(string equipmentName, int windowMinutes, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default, ParamTuneSettingsResponse? tuneSettings = null)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);
        var queryParts = new List<string>{$"windowMinutes={windowMinutes}"};

        void AddQuery(string name, string? value)
        {
            if (value is not null)
            {
                queryParts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        void AddQueryDouble(string name, double? value)
        {
            if (value.HasValue)
            {
                AddQuery(name, value.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (fromUtc.HasValue)
        {
            AddQuery("fromUtc", ToQueryUtc(fromUtc.Value));
        }

        if (toUtc.HasValue)
        {
            AddQuery("toUtc", ToQueryUtc(toUtc.Value));
        }

        if (tuneSettings is not null)
        {
            AddQuery("pv", tuneSettings.Pv);
            AddQueryDouble("pvMin", tuneSettings.PvMin);
            AddQueryDouble("pvMax", tuneSettings.PvMax);
            AddQuery("sp", tuneSettings.Sp);
            AddQueryDouble("spMin", tuneSettings.SpMin);
            AddQueryDouble("spMax", tuneSettings.SpMax);

            // Test Kp передаётся как обычный online-тег. Никаких min/max и trend параметров для него нет.
            AddQuery("testKpTag", tuneSettings.TestKpTag);
        }

        var query = string.Join("&", queryParts);
        var result = await client.GetFromJsonAsync<ParamTuneRuntimeResponse>($"api/param/{encodedName}/tune?{query}", ct);

        return result
            ?? new ParamTuneRuntimeResponse
            {
                EquipmentName = equipmentName,
                Supported = false,
                Message = "Runtime Service returned empty PID Tune response."
            };
    }

    /// <summary>Читает историю произвольного Output через общий источник Param trends.</summary>
    public async Task<ParamTrendResponse> GetTagTrendAsync(string tagName, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var client = CreateClient();
        var request = new ParamTagTrendRequest { TagName = tagName, FromUtc = fromUtc, ToUtc = toUtc };

        using var response = await client.PostAsJsonAsync("api/param/tags/trend", request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ParamTrendResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("Runtime returned an empty trend response.");
    }

    /// <summary>
    /// Проверяет произвольный числовой Plant SCADA Variable Tag через Runtime.Service.
    ///
    /// request.RequireTrend = false:
    /// выполняется обычная online-проверка числового тега.
    ///
    /// request.RequireTrend = true:
    /// кроме текущего числового значения требуется trend-reference.
    ///
    /// Метод общий и не требует equipmentName.
    /// </summary>
    public async Task<ParamTagCheckResponse> CheckNumericTagAsync(ParamTagCheckRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();

        using var response = await client.PostAsJsonAsync("api/param/tags/check", request, ct);
        var result = await response.Content.ReadFromJsonAsync<ParamTagCheckResponse>(cancellationToken: ct);

        return result ?? new ParamTagCheckResponse
        {
            TagName = request.TagName,
            Found = false,
            TrendRequired = request.RequireTrend,
            TrendFound = false,
            Message = response.IsSuccessStatusCode ? "Runtime Service returned empty numeric tag check response." : $"Runtime Service rejected numeric tag check: {(int)response.StatusCode} {response.ReasonPhrase}"
        };
    }

    /// <summary>
    /// Сохраняет PV/SP теги и диапазоны в Runtime/PostgreSQL.
    /// </summary>
    public async Task<ParamTuneSettingsResponse> SaveTuneAsync(string equipmentName, ParamTuneSaveRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        using var response = await client.PostAsJsonAsync($"api/param/{encodedName}/tune", request, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ParamTuneSettingsResponse>(cancellationToken: ct);
        return result ?? new ParamTuneSettingsResponse { EquipmentName = equipmentName };
    }

    /// <summary>
    /// Читает PLC reference page.
    /// </summary>
    public async Task<ParamPlcRefsResponse> GetPlcRefsAsync(string equipmentName, CancellationToken ct = default)
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
    public async Task<ParamDiDoRefsResponse> GetDiDoRefsAsync(string equipmentName, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);

        var result = await client.GetFromJsonAsync<ParamDiDoRefsResponse>($"api/param/{encodedName}/refs/dido", ct);

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
    public async Task<ParamDryRunResponse> GetDryRunAsync(string equipmentName, CancellationToken ct = default)
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
    public async Task<ParamAtvRefResponse> GetAtvRefAsync(string equipmentName, CancellationToken ct = default)
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
    public async Task<ParamWriteResponse> WriteAsync(string equipmentName, ParamWriteRequest request, CancellationToken ct = default)
    {
        var client = CreateClient();
        var encodedName = Uri.EscapeDataString(equipmentName);
        await EnrichWriteActorAsync(request);

        using var response = await client.PostAsJsonAsync(
            $"api/param/{encodedName}/write",
            request,
            ct);

        try
        {
            var result = await response.Content.ReadFromJsonAsync<ParamWriteResponse>(cancellationToken: ct);
            if (result is not null)
                return result;
        }
        catch (System.Text.Json.JsonException)
        {
            // Runtime.Service normally returns ParamWriteResponse even for BadRequest.
            // If a proxy/NotFound/empty body is returned, build a structured failure so UI can show the real HTTP reason.
        }

        return new ParamWriteResponse
        {
            EquipmentName = equipmentName,
            ItemName = request.ItemName,
            Actor = request.Actor,
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
    /// Добавляет к write-запросу Windows-пользователя и его группы.
    /// Отображаемые роли TechMES не подмешиваются в ActorGroups.
    /// </summary>
    private async Task EnrichWriteActorAsync(ParamWriteRequest request)
    {
        var principal = await ReadCurrentPrincipalAsync();
        var actor = ReadActorName(principal);

        if (string.IsNullOrWhiteSpace(actor))
        {
            request.Actor = null;
            request.ActorGroups = [];
            return;
        }

        request.Actor = actor.Trim();
        request.ActorGroups = WindowsPrincipalGroups.Read(principal).Where(group => !string.IsNullOrWhiteSpace(group)).Select(group => group!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Заполняет Windows-пользователя и его группы для других write-модулей.
    /// Использует только эффективный Blazor authentication state, чтобы read-only режим не обходился через HttpContext.
    /// HTTP-запросов и записи в SCADA этот метод не выполняет.
    /// </summary>
    public async Task FillWriteActorAsync(ParamWriteRequest request)
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();

        // Сохраняем существующую проверку: редактирование требует авторизованного пользователя.
        if (state.User.Identity?.IsAuthenticated != true)
            throw new InvalidOperationException("Sign in to edit recipe values.");

        request.Actor = ReadActorName(state.User)?.Trim();

        // Используем общий механизм чтения Windows-групп вместо удалённого локального метода.
        request.ActorGroups = WindowsPrincipalGroups.Read(state.User).Where(group => !string.IsNullOrWhiteSpace(group)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Читает текущий ClaimsPrincipal сначала из Blazor authentication state, затем из HttpContext.
    /// </summary>
    private async Task<ClaimsPrincipal?> ReadCurrentPrincipalAsync()
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        if (authenticationState.User.Identity?.IsAuthenticated == true)
            return authenticationState.User;

        var httpUser = _httpContextAccessor.HttpContext?.User;
        return httpUser?.Identity?.IsAuthenticated == true
            ? httpUser
            : null;
    }

    /// <summary>
    /// Возвращает имя Windows-пользователя в формате DOMAIN\User, если оно доступно.
    /// </summary>
    private static string? ReadActorName(ClaimsPrincipal? principal)
    {
        return principal?.Identity?.IsAuthenticated == true
            ? principal.Identity.Name
            : null;
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
