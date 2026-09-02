using TechMES.Application.Calc;
using TechMES.Application.Equipment;
using TechMES.Application.Param;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Runtime.Service.Runtime;
using Microsoft.Extensions.Options;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP API Param-модуля.
/// Этот слой не знает деталей CtApi: он только находит EquipmentDto в каталоге
/// и передает запрос в IEquipmentParamProvider.
/// </summary>
public static class ParamEndpoints
{
    /// <summary>
    /// Сохраняет PV/SP теги и их диапазоны для конкретного VGA.
    /// </summary>
    public static IEndpointRouteBuilder MapParamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/param/{equipmentName}/snapshot", GetSnapshotAsync);
        app.MapGet("/api/param/{equipmentName}/trend", GetTrendAsync);
        app.MapPost("/api/param/tags/check", CheckNumericTagAsync); // Общая проверка уже разрешённого числового Variable Tag. Она не принадлежит конкретному Equipment и используется разными модулями.
        app.MapGet("/api/param/{equipmentName}/tune", GetTuneAsync);
        app.MapPost("/api/param/{equipmentName}/tune", SaveTuneAsync);
        app.MapGet("/api/param/{equipmentName}/refs/plc", GetPlcRefsAsync);
        app.MapGet("/api/param/{equipmentName}/refs/dido", GetDiDoRefsAsync);
        app.MapGet("/api/param/{equipmentName}/refs/dryrun", GetDryRunAsync);
        app.MapGet("/api/param/{equipmentName}/refs/atv", GetAtvRefAsync);
        app.MapPost("/api/param/{equipmentName}/write", WriteAsync);

        return app;
    }

    /// <summary>
    /// Возвращает текущие Param-значения и список доступных вкладок.
    /// Этот endpoint вызывается периодически, пока пользователь находится в Param-зоне.
    /// </summary>
    private static async Task<IResult> GetSnapshotAsync(string equipmentName, IEquipmentCatalogProvider equipmentCatalog, IEquipmentParamProvider paramProvider, CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var result = await paramProvider.GetSnapshotAsync(equipment, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает Runtime-данные вкладки PID Tune для VGA-оборудования.
    /// PV/SP/Test Kp можно временно передать из WEB после Check,
    /// не сохраняя черновик в PostgreSQL.
    /// </summary>
    private static async Task<IResult> GetTuneAsync(string equipmentName, int? windowMinutes, DateTime? fromUtc, DateTime? toUtc, string? pv, double? pvMin, double? pvMax, string? sp,double? spMin,
        double? spMax, string? testKpTag, IEquipmentCatalogProvider equipmentCatalog, IEquipmentParamProvider paramProvider, IParamTuneStore tuneStore, CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var settings = await tuneStore.GetAsync(equipment.Name, ct) ?? new ParamTuneSettingsResponse
            {
                EquipmentName = equipment.Name
            };

        ApplyTuneQueryOverrides(settings, pv, pvMin, pvMax, sp, spMin, spMax, testKpTag);
        var result = await paramProvider.GetTuneRuntimeAsync(equipment, settings, windowMinutes.GetValueOrDefault(30), fromUtc, toUtc, ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Подставляет проверенные в UI PV/SP/Test Kp теги в Runtime-расчет
    /// без сохранения в PostgreSQL.
    ///
    /// PV/SP продолжают использоваться как trend-теги.
    /// Test Kp используется только как online numeric tag.
    /// </summary>
    private static void ApplyTuneQueryOverrides(ParamTuneSettingsResponse settings, string? pv, double? pvMin, double? pvMax, string? sp, double? spMin, double? spMax, string? testKpTag)
    {
        if (pv is not null)
        {
            settings.Pv = string.IsNullOrWhiteSpace(pv) ? null : pv.Trim();
        }

        if (pvMin.HasValue)
            settings.PvMin = pvMin;

        if (pvMax.HasValue)
            settings.PvMax = pvMax;

        if (sp is not null)
        {
            settings.Sp = string.IsNullOrWhiteSpace(sp) ? null : sp.Trim();
        }

        if (spMin.HasValue)
            settings.SpMin = spMin;

        if (spMax.HasValue)
            settings.SpMax = spMax;

        if (testKpTag is not null)
        {
            settings.TestKpTag = string.IsNullOrWhiteSpace(testKpTag) ? null : testKpTag.Trim();
        }
    }

    /// <summary>
    /// Проверяет произвольный числовой Plant SCADA Variable Tag.
    ///
    /// Endpoint намеренно не принимает equipmentName.
    ///
    /// На этом уровне тег уже должен быть разрешён:
    /// - PID Tune передаёт PV/SP/Test Kp;
    /// - Density после fallback передаст Temperature/Pressure Variable Tag.
    ///
    /// Проверка конкретного Equipment здесь не нужна.
    /// Если вызывающему коду требуется сначала разрешить Equipment + ITEM,
    /// он использует существующий Param snapshot endpoint.
    /// </summary>
    private static async Task<IResult> CheckNumericTagAsync(ParamTagCheckRequest request, IEquipmentParamProvider paramProvider, CancellationToken ct)
    {
        var result = await paramProvider.CheckNumericTagAsync(request.TagName, request.RequireTrend, ct);
        return Results.Ok(result);
    }


    /// <summary>
    /// Сохраняет PV/SP теги и их диапазоны для конкретного VGA.
    /// </summary>
    private static async Task<IResult> SaveTuneAsync(string equipmentName, ParamTuneSaveRequest request, IEquipmentCatalogProvider equipmentCatalog, IParamTuneStore tuneStore, CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        if (equipment.TypeGroup != EquipmentTypeGroup.VGA)
            return Results.BadRequest("PID Tune is supported only for VGA equipment.");

        var result = await tuneStore.SaveAsync(equipment.Name, request, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает trend-точки для графика.
    /// fromUtc/toUtc используются, когда пользователь прокручивает историю ECharts.
    /// </summary>
    private static async Task<IResult> GetTrendAsync(
        string equipmentName,
        int? windowMinutes,
        DateTime? fromUtc,
        DateTime? toUtc,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var result = await paramProvider.GetTrendAsync(
            equipment,
            windowMinutes.GetValueOrDefault(30),
            fromUtc,
            toUtc,
            ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает PLC reference page для текущего оборудования.
    /// </summary>
    private static async Task<IResult> GetPlcRefsAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var result = await paramProvider.GetPlcRefsAsync(equipment, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает DI/DO reference page. Для разрешения ссылок нужен полный каталог.
    /// </summary>
    private static async Task<IResult> GetDiDoRefsAsync(
        string equipmentName,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentParamProvider paramProvider,
        CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var result = await paramProvider.GetDiDoRefsAsync(
            equipment,
            catalog.Equipments,
            ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает DryRun reference page и связанные DI-узлы.
    /// </summary>
    private static async Task<IResult> GetDryRunAsync(string equipmentName, IEquipmentCatalogProvider equipmentCatalog, IEquipmentParamProvider paramProvider, CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var result = await paramProvider.GetDryRunAsync(equipment, catalog.Equipments, ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Возвращает связанную ATV page, если оборудование имеет ссылку на частотник.
    /// </summary>
    private static async Task<IResult> GetAtvRefAsync(string equipmentName, IEquipmentCatalogProvider equipmentCatalog, IEquipmentParamProvider paramProvider, CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            return Results.NotFound();

        var catalog = await equipmentCatalog.GetEquipmentListAsync(ct);
        var result = await paramProvider.GetAtvRefAsync(equipment, catalog.Equipments, ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Выполняет запись одного Param item.
    /// Endpoint намеренно повторно разрешает оборудование по имени и не доверяет только данным WEB.
    /// </summary>
    private static async Task<IResult> WriteAsync(string equipmentName, ParamWriteRequest request, IEquipmentCatalogProvider equipmentCatalog, ICalcModelCatalogProvider calcModelCatalog, IEquipmentParamProvider paramProvider, IAppRuntimeContext runtimeContext, IOptions<ParamWriteOptions> writeOptions, CancellationToken ct)
    {
        var equipment = await equipmentCatalog.GetEquipmentByNameAsync(equipmentName, ct);

        if (equipment is null)
            equipment = await TryResolveContentEquipmentAsync(equipmentName, request.ItemName, calcModelCatalog, ct);

        if (equipment is null)
        {
            if (!IsDryRunWriteItem(request.ItemName) && !IsReferenceWriteRequest(request))
                return Results.NotFound();

            // DryRun/PLC reference settings can be stored on equipment that is not present in the visible WEB catalog.
            // CtApi can still resolve its EquipItem tag by name; provider-level validation below decides whether the write is allowed.
            equipment = new EquipmentDto
            {
                Name = equipmentName,
                DisplayName = equipmentName,
                TypeGroup = EquipmentTypeGroup.Unknown,
                IsGroup = false
            };
        }

        var requestedActor = request.Actor?.Trim();
        request.Actor = requestedActor;

        var authorizationFailure = AuthorizeWrite(equipment, request, writeOptions.Value);
        if (authorizationFailure is not null)
            return Results.Json(authorizationFailure, statusCode: StatusCodes.Status403Forbidden);

        request.Actor = string.IsNullOrWhiteSpace(requestedActor)
            ? runtimeContext.DeviceName
            : requestedActor;

        var result = await paramProvider.WriteAsync(equipment, request, ct);
        result.Actor = request.Actor;

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }

    /// <summary>
    /// Разрешает Content только из уже проверенного Calc Catalog.
    /// Content не добавляется в обычное Equipment tree, но после этой проверки использует общий Param write-flow.
    /// </summary>
    private static async Task<EquipmentDto?> TryResolveContentEquipmentAsync(string equipmentName, string itemName, ICalcModelCatalogProvider calcModelCatalog, CancellationToken ct)
    {
        var catalog = await calcModelCatalog.GetSnapshotAsync(ct);
        var model = catalog.Items.FirstOrDefault(item => item.Type == CalcModelTypeDto.Content && item.Name.Equals(equipmentName, StringComparison.OrdinalIgnoreCase));

        if (model is null || !model.ItemTags.Keys.Any(key => key.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
            return null;

        return new EquipmentDto
        {
            Name = model.Name,
            DisplayName = model.Name,
            Description = model.Description,
            Station = model.Station,
            TypeName = CalcModelTypeDto.Content.ToString(),
            TypeGroup = EquipmentTypeGroup.Content,
            IsGroup = false
        };
    }

    /// <summary>
    /// Выполняет Runtime-side enforcement Windows-пользователей и групп.
    /// WEB передает Actor/ActorGroups из Windows identity, но окончательное решение всегда остается здесь.
    /// </summary>
    private static ParamWriteResponse? AuthorizeWrite(EquipmentDto equipment, ParamWriteRequest request, ParamWriteOptions options)
    {
        var authorization = options.Authorization;
        if (!authorization.Enabled)
            return null;

        if (authorization.RequireWindowsUser && string.IsNullOrWhiteSpace(request.Actor))
            return BuildAuthorizationFailure(equipment, request, "Param write is denied: Windows user was not provided.");

        var allowedUsers = SplitAllowList(authorization.AllowedUsers);
        var allowedGroups = SplitAllowList(authorization.AllowedGroups);

        if (allowedUsers.Count == 0 && allowedGroups.Count == 0)
            return BuildAuthorizationFailure(equipment, request, "Param write is denied: Windows authorization is enabled, but allowed users/groups are empty.");

        if (!string.IsNullOrWhiteSpace(request.Actor) && allowedUsers.Any(allowedUser => PrincipalMatches(request.Actor, allowedUser)))
            return null;

        if (request.ActorGroups.Any(actorGroup => allowedGroups.Any(allowedGroup => PrincipalMatches(actorGroup, allowedGroup))))
            return null;

        return BuildAuthorizationFailure(equipment, request, $"Param write is denied for Windows user '{request.Actor}'.");
    }

    /// <summary>
    /// Формирует единый ответ отказа, чтобы WEB показывал оператору ту же структуру, что и при CtApi-ошибках.
    /// </summary>
    private static ParamWriteResponse BuildAuthorizationFailure(EquipmentDto equipment, ParamWriteRequest request, string error)
    {
        return new ParamWriteResponse
        {
            EquipmentName = equipment.Name,
            TypeGroup = equipment.TypeGroup,
            ItemName = request.ItemName,
            Actor = request.Actor,
            Success = false,
            DryRun = false,
            Error = error,
            Message = "Param write was blocked by Windows authorization."
        };
    }

    /// <summary>
    /// Разбирает ';' и ',' allow-list строки из appsettings.
    /// </summary>
    private static List<string> SplitAllowList(string? value)
    {
        return (value ?? "")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Сравнивает Windows principal с учетом полного DOMAIN\Name и короткого Name.
    /// Это удобно, когда группа локальная на сервере, а в настройке указан только ее короткий alias.
    /// </summary>
    private static bool PrincipalMatches(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            return false;

        var normalizedActual = actual.Trim();
        var normalizedExpected = expected.Trim();

        if (normalizedActual.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
            return true;

        var actualShortName = normalizedActual.Split('\\').LastOrDefault() ?? normalizedActual;
        var expectedShortName = normalizedExpected.Split('\\').LastOrDefault() ?? normalizedExpected;

        return actualShortName.Equals(expectedShortName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Проверяет DryRun write item names на endpoint-уровне, чтобы разрешить запись reference equipment,
    /// которого может не быть в основном каталоге оборудования.
    /// </summary>
    private static bool IsDryRunWriteItem(string? itemName)
    {
        return itemName is not null
            && (itemName.Equals("DryRunAEn", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("DryRunLimToOff", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("DryRunTimeToOn", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("DryRunTimeToOff", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Проверяет, что WEB просит reference-write из PLC вкладки.
    /// Полное право записи не определяется здесь: CtApi provider заново читает TabPLC
    /// исходного оборудования и ищет там целевую строку.
    /// </summary>
    private static bool IsReferenceWriteRequest(ParamWriteRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ReferenceSourceEquipmentName)
            && request.ReferenceValueKind.HasValue
            && !string.IsNullOrWhiteSpace(request.ItemName);
    }
}
