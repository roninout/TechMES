using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Components.Authorization;
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
    /// Добавляет к write-запросу Windows-пользователя и группы.
    /// Runtime.Service использует эти данные для allow-list enforcement, а WEB остается только транспортным слоем.
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

        request.ActorGroups = ReadActorGroups(principal)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
    /// Собирает Windows-группы из WindowsIdentity и claims.
    /// SID-группы переводятся в DOMAIN\Group, чтобы appsettings оставался читаемым.
    /// </summary>
    private static IEnumerable<string> ReadActorGroups(ClaimsPrincipal? principal)
    {
        if (OperatingSystem.IsWindows() && principal?.Identity is WindowsIdentity windowsIdentity)
        {
            foreach (var group in ReadWindowsIdentityGroups(windowsIdentity))
                yield return group;
        }

        if (principal is null)
            yield break;

        foreach (var claim in principal.Claims)
        {
            if (claim.Type == ClaimTypes.Role
                || claim.Type == ClaimTypes.GroupSid
                || claim.Type.EndsWith("/groupsid", StringComparison.OrdinalIgnoreCase))
            {
                var translated = OperatingSystem.IsWindows()
                    ? TryTranslateSid(claim.Value)
                    : null;
                yield return translated ?? claim.Value;
            }
        }
    }

    /// <summary>
    /// Безопасно читает группы WindowsIdentity: некоторые SID могут не переводиться на локальной машине.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadWindowsIdentityGroups(WindowsIdentity windowsIdentity)
    {
        if (windowsIdentity.Groups is null)
            yield break;

        foreach (var group in windowsIdentity.Groups)
        {
            var translated = TryTranslateIdentityReference(group);
            if (!string.IsNullOrWhiteSpace(translated))
                yield return translated;
        }
    }

    /// <summary>
    /// Переводит SID-строку в DOMAIN\Name, если Windows может ее разрешить.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryTranslateSid(string value)
    {
        try
        {
            return new SecurityIdentifier(value).Translate(typeof(NTAccount)).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// Переводит IdentityReference в читаемое имя Windows-группы.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryTranslateIdentityReference(IdentityReference reference)
    {
        try
        {
            return reference.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return reference.Value;
        }
        catch (SystemException)
        {
            return reference.Value;
        }
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
