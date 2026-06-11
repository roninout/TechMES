using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Application.Param;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Infrastructure.CtApi.Native;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Основная CtApi-реализация Param-модуля.
/// Класс переводит доменную модель оборудования в Plant SCADA tag/trend/ref запросы,
/// нормализует ответы CtApi в DTO и изолирует Runtime.Service от нативных деталей CtApi.dll.
/// </summary>
public sealed class CtApiEquipmentParamProvider : IEquipmentParamProvider
{
    /// <summary>
    /// Формат чисел, который ожидает Plant SCADA/Cicode: точка как разделитель и invariant culture.
    /// </summary>
    private static readonly CultureInfo CitectNumberFormat = CultureInfo.InvariantCulture;

    /// <summary>
    /// Тонкая обертка над CtApi.dll. Все реальные TagRead/TagWrite/Cicode идут через нее.
    /// </summary>
    private readonly ICtApiNativeClient _nativeClient;

    /// <summary>
    /// Общие настройки CtApi: источник данных, AllowWrites, параллелизм чтения и health tag.
    /// </summary>
    private readonly IOptions<CtApiOptions> _options;

    /// <summary>
    /// Настройки write-flow Param: Enabled, DryRun, RequireComment и AuditEnabled.
    /// </summary>
    private readonly IOptions<ParamWriteOptions> _writeOptions;

    /// <summary>
    /// Логгер для CtApi ошибок, которые важно увидеть в Runtime.Service console.
    /// </summary>
    private readonly ILogger<CtApiEquipmentParamProvider> _logger;

    /// <summary>
    /// Кэш Equipment+EquipItem -> TagName. Снижает нагрузку на CtApi при частом polling.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _tagNameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Кэш единиц измерения по TagName.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _tagUnitCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Кэш Equipment+TrendItem -> TrendName для графиков Param.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _trendNameCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<ParamItemDefinition> DryRunItems =
    [
        new("DryRunAEn", ParamValueKind.Boolean),
        new("DryRunA", ParamValueKind.Boolean),
        new("DryRunLimToOff", ParamValueKind.Number),
        new("DryRunTimeToOn", ParamValueKind.Number),
        new("DryRunTimeToOff", ParamValueKind.Number)
    ];

    public CtApiEquipmentParamProvider(
        ICtApiNativeClient nativeClient,
        IOptions<CtApiOptions> options,
        IOptions<ParamWriteOptions> writeOptions,
        ILogger<CtApiEquipmentParamProvider> logger)
    {
        _nativeClient = nativeClient;
        _options = options;
        _writeOptions = writeOptions;
        _logger = logger;
    }

    public async Task<ParamSnapshotResponse> GetSnapshotAsync(
        EquipmentDto equipment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        if (!ParamDefinitions.TryGet(equipment.TypeGroup, out var definition))
        {
            return new ParamSnapshotResponse
            {
                EquipmentName = equipment.Name,
                TypeName = equipment.TypeName,
                TypeGroup = equipment.TypeGroup,
                Supported = false,
                Message = $"Param read-only is not configured for type group '{equipment.TypeGroup}'.",
                Time = DateTime.Now
            };
        }

        var response = new ParamSnapshotResponse
        {
            EquipmentName = equipment.Name,
            TypeName = equipment.TypeName,
            TypeGroup = equipment.TypeGroup,
            Supported = true,
            Location = equipment.Location,
            Time = DateTime.Now,
            Pages = definition.Pages.ToList(),
            TrendItems = definition.TrendItems.Select(ToDto).ToList()
        };

        foreach (var item in definition.Items)
        {
            ct.ThrowIfCancellationRequested();

            var tagName = await ResolveTagNameAsync(equipment.Name, item.Name, ct);
            if (string.IsNullOrWhiteSpace(tagName)
                || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? raw;
            try
            {
                raw = await _nativeClient.TagReadAsync(tagName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Param TagRead failed. Equipment={Equipment}, Item={Item}, Tag={Tag}",
                    equipment.Name,
                    item.Name,
                    tagName);
                continue;
            }

            var dto = BuildItemDto(item, tagName, raw);
            dto.CanWrite = ParamWriteDefinitions.CanWrite(equipment.TypeGroup, item.Name);

            if (item.Name.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                var unit = await ResolveUnitAsync(tagName, ct);
                dto.Unit = unit;
                response.Unit = unit;
            }

            response.Items.Add(dto);
        }

        if (response.Items.Count == 0)
        {
            response.Supported = false;
            response.Message = "No readable Param tags were found for this equipment.";
        }

        return response;
    }

    public async Task<ParamTrendResponse> GetTrendAsync(
        EquipmentDto equipment,
        int windowMinutes = 30,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        windowMinutes = Math.Clamp(windowMinutes, 1, 240);

        var to = NormalizeUtc(toUtc) ?? DateTime.UtcNow;
        var from = NormalizeUtc(fromUtc) ?? to.AddMinutes(-windowMinutes);

        if (from >= to)
            from = to.AddMinutes(-windowMinutes);

        if (!ParamDefinitions.TryGet(equipment.TypeGroup, out var definition)
            || definition.TrendItems.Count == 0)
        {
            return new ParamTrendResponse
            {
                EquipmentName = equipment.Name,
                TypeGroup = equipment.TypeGroup,
                Supported = false,
                Message = "Trend definition is not configured for this equipment type.",
                FromUtc = from,
                ToUtc = to
            };
        }

        var snapshot = await GetSnapshotAsync(equipment, ct);
        if (!snapshot.Supported)
        {
            return new ParamTrendResponse
            {
                EquipmentName = equipment.Name,
                TypeGroup = equipment.TypeGroup,
                Supported = false,
                Message = snapshot.Message,
                FromUtc = from,
                ToUtc = to
            };
        }

        var baseItem = definition.TrendItems[0];
        var (axisMin, axisMax) = ResolveBaseRange(baseItem, snapshot);

        var response = new ParamTrendResponse
        {
            EquipmentName = equipment.Name,
            TypeGroup = equipment.TypeGroup,
            Supported = true,
            FromUtc = from,
            ToUtc = to,
            AxisYMin = axisMin,
            AxisYMax = axisMax,
            Series = definition.TrendItems.Select(ToDto).ToList()
        };

        foreach (var trendItem in definition.TrendItems)
        {
            ct.ThrowIfCancellationRequested();

            var trendRef = await ResolveTrendNameAsync(equipment.Name, trendItem.Name, ct);
            if (trendRef is null)
                continue;

            var nativeRange = ResolveNativeRange(trendItem, axisMin, axisMax);
            var rows = await QueryTrendRowsAsync(trendRef.Value, from, to, ct);

            foreach (var row in rows)
            {
                var plotValue = trendItem.Name.Equals(baseItem.Name, StringComparison.OrdinalIgnoreCase)
                    ? row.Value
                    : MapToBase(row.Value, nativeRange.Min, nativeRange.Max, axisMin, axisMax);

                response.Points.Add(new ParamTrendPointDto
                {
                    Series = trendItem.Name,
                    Time = row.TimeUtc.ToLocalTime(),
                    RawValue = row.Value,
                    Value = plotValue,
                    Quality = row.Quality
                });
            }
        }

        response.Points = response.Points
            .OrderBy(x => x.Time)
            .ThenBy(x => x.Series, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (response.Points.Count == 0)
        {
            response.Message = "No trend points were returned for the selected time window.";
        }

        return response;
    }

    public async Task<ParamPlcRefsResponse> GetPlcRefsAsync(
        EquipmentDto equipment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var response = new ParamPlcRefsResponse
        {
            EquipmentName = equipment.Name,
            Supported = true,
            Time = DateTime.Now
        };

        var refs = await BrowseEquipmentRefsAsync(
            equipment.Name,
            category: "TabPLC",
            equipmentItem: "State",
            fields: ["REFEQUIP", "REFITEM", "CUSTOM1", "COMMENT"],
            ct);

        foreach (var row in refs)
        {
            var refEquipment = CleanRefValue(GetValue(row, "REFEQUIP"));
            if (string.IsNullOrWhiteSpace(refEquipment))
                continue;

            var refItem = CleanRefValue(GetValue(row, "REFITEM"));
            var type = ParsePlcType(GetValue(row, "CUSTOM1"));
            var comment = CleanScadaText(GetValue(row, "COMMENT"));
            var itemForTag = GetPlcEquipItemForTagInfo(refItem, type);

            var tagName = await ResolveTagNameAsync(refEquipment, itemForTag, ct);
            var unit = await ResolveUnitAsync(tagName, ct);
            var forcedTagName = "";

            if (type is ParamPlcType.EqDigital or ParamPlcType.EqDigitalInOut)
            {
                forcedTagName = await ResolveTagNameAsync(refEquipment, "ForceCmd", ct);
            }

            response.Rows.Add(new ParamPlcRefDto
            {
                EquipmentName = refEquipment,
                RefItem = refItem,
                Type = type,
                Comment = comment,
                TagName = tagName,
                Unit = unit,
                ForcedTagName = forcedTagName
            });
        }

        if (response.Rows.Count == 0)
        {
            response.Supported = false;
            response.Message = "No PLC references were found for this equipment.";
            return response;
        }

        var tagsToRead = response.Rows
            .SelectMany(row => new[] { row.TagName, row.ForcedTagName })
            .Where(IsReadableTagName)
            .Select(tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rawMap = await TagReadManyAsync(tagsToRead, ct);

        foreach (var row in response.Rows)
        {
            if (IsReadableTagName(row.TagName)
                && rawMap.TryGetValue(row.TagName!, out var raw)
                && raw is not null)
            {
                ApplyValue(row, raw);
            }

            if (IsReadableTagName(row.ForcedTagName)
                && rawMap.TryGetValue(row.ForcedTagName!, out var forcedRaw)
                && forcedRaw is not null
                && TryParseBoolean(forcedRaw, out var forceCmd))
            {
                row.ForceCmd = forceCmd;
            }
        }

        response.Rows = response.Rows
            .OrderBy(row => row.EquipmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RefItem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Type.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return response;
    }

    public async Task<ParamDiDoRefsResponse> GetDiDoRefsAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var response = new ParamDiDoRefsResponse
        {
            EquipmentName = equipment.Name,
            Supported = true,
            Time = DateTime.Now
        };

        var catalogLookup = BuildEquipmentLookup(equipmentCatalog);

        var refs = await BrowseEquipmentRefsAsync(
            equipment.Name,
            category: "TabDIDO",
            equipmentItem: "State",
            fields: ["REFEQUIP"],
            ct);

        var refNames = refs
            .Select(row => CleanRefValue(GetValue(row, "REFEQUIP")))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var refName in refNames)
        {
            ct.ThrowIfCancellationRequested();

            if (!catalogLookup.TryGetValue(refName, out var linkedEquipment))
                continue;

            if (linkedEquipment.TypeGroup is not (EquipmentTypeGroup.DI or EquipmentTypeGroup.DO))
                continue;

            var snapshot = await GetSnapshotAsync(linkedEquipment, ct);
            if (!snapshot.Supported)
                continue;

            var dto = BuildDiDoRef(linkedEquipment, snapshot);

            if (linkedEquipment.TypeGroup == EquipmentTypeGroup.DI)
                response.DiRows.Add(dto);
            else
                response.DoRows.Add(dto);
        }

        response.DiRows = response.DiRows
            .OrderBy(GetChanelSortKey)
            .ThenBy(row => row.EquipmentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        response.DoRows = response.DoRows
            .OrderBy(GetChanelSortKey)
            .ThenBy(row => row.EquipmentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (response.DiRows.Count == 0 && response.DoRows.Count == 0)
        {
            response.Supported = false;
            response.Message = "No DI/DO references were found for this equipment.";
        }

        return response;
    }

    public async Task<ParamDryRunResponse> GetDryRunAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var response = new ParamDryRunResponse
        {
            EquipmentName = equipment.Name,
            Supported = true,
            Time = DateTime.Now
        };

        if (equipment.TypeGroup != EquipmentTypeGroup.Motor)
        {
            response.Supported = false;
            response.Message = "DryRun page is available only for Motor equipment.";
            return response;
        }

        var catalogLookup = BuildEquipmentLookup(equipmentCatalog);
        var dryRunRef = await GetWinOpenedRefAsync(
            equipment.Name,
            equipmentItem: "State",
            assocExpected: "__EquipmentPump",
            ct);

        if (dryRunRef is null)
        {
            response.Supported = false;
            response.Message = "No DryRun is configured for this equipment.";
            return response;
        }

        var dryRunEquipmentName = dryRunRef.Value.RefEquipment;
        var dryRunEquipment = FindEquipment(catalogLookup, dryRunEquipmentName);

        response.DryRunEquipmentName = dryRunEquipmentName;
        response.DryRunEquipment = await ReadLinkedItemsAsync(
            dryRunEquipment,
            dryRunEquipmentName,
            DryRunItems,
            ct);

        response.LinkedDi = await TryResolveDryRunDiAsync(
            dryRunEquipmentName,
            catalogLookup,
            ct);

        response.LinkedAi = await TryResolveDryRunAiAsync(
            dryRunEquipmentName,
            catalogLookup,
            ct);

        return response;
    }

    public async Task<ParamAtvRefResponse> GetAtvRefAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        var response = new ParamAtvRefResponse
        {
            EquipmentName = equipment.Name,
            Supported = true,
            Time = DateTime.Now
        };

        var catalogLookup = BuildEquipmentLookup(equipmentCatalog);
        EquipmentDto? atvEquipment;

        if (equipment.TypeGroup == EquipmentTypeGroup.ATV)
        {
            atvEquipment = equipment;
            response.IsLinkedFromMotor = false;
        }
        else if (equipment.TypeGroup == EquipmentTypeGroup.Motor)
        {
            var atvRef = await GetWinOpenedRefAsync(
                equipment.Name,
                equipmentItem: "State",
                assocExpected: "__EquipmentSic",
                ct);

            if (atvRef is null)
            {
                response.Supported = false;
                response.Message = "No ATV is configured for this equipment.";
                return response;
            }

            atvEquipment = FindEquipment(catalogLookup, atvRef.Value.RefEquipment);
            response.IsLinkedFromMotor = true;
        }
        else
        {
            response.Supported = false;
            response.Message = "ATV page is available only for Motor and ATV equipment.";
            return response;
        }

        if (atvEquipment is null || atvEquipment.TypeGroup != EquipmentTypeGroup.ATV)
        {
            response.Supported = false;
            response.Message = "Linked ATV equipment was not found in the catalog.";
            return response;
        }

        var snapshot = await GetSnapshotAsync(atvEquipment, ct);
        if (!snapshot.Supported)
        {
            response.Supported = false;
            response.Message = snapshot.Message ?? "Linked ATV Param tags are not readable.";
            response.AtvEquipmentName = atvEquipment.Name;
            return response;
        }

        response.AtvEquipmentName = atvEquipment.Name;
        response.AtvEquipment = BuildLinkedParam(atvEquipment, snapshot);
        return response;
    }

    public async Task<ParamWriteResponse> WriteAsync(
        EquipmentDto equipment,
        ParamWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(request);

        // Ответ создается сразу: дальше каждый guard возвращает структурированную причину отказа,
        // чтобы WEB мог показать оператору не общий HTTP error, а конкретную проблему.
        var response = new ParamWriteResponse
        {
            EquipmentName = equipment.Name,
            TypeGroup = equipment.TypeGroup,
            ItemName = (request.ItemName ?? "").Trim(),
            Time = DateTime.Now
        };

        if (equipment.IsGroup)
            return WriteFailed(response, "Param write is not available for Equipment group nodes.");

        if (string.IsNullOrWhiteSpace(response.ItemName))
            return WriteFailed(response, "Param item name is required.");

        if (!ParamDefinitions.TryGet(equipment.TypeGroup, out var definition))
            return WriteFailed(response, $"Param write is not configured for type group '{equipment.TypeGroup}'.");

        var itemDefinition = definition.Items.FirstOrDefault(item =>
            item.Name.Equals(response.ItemName, StringComparison.OrdinalIgnoreCase));

        if (itemDefinition is null)
            return WriteFailed(response, $"Param item '{response.ItemName}' is not configured for '{equipment.TypeGroup}'.");

        if (!ParamWriteDefinitions.CanWrite(equipment.TypeGroup, itemDefinition.Name))
            return WriteFailed(response, $"Param item '{itemDefinition.Name}' is read-only.");

        var options = _writeOptions.Value;
        if (options.RequireComment && string.IsNullOrWhiteSpace(request.Comment))
            return WriteFailed(response, "Comment is required for Param write.");

        // Нормализация выполняется на сервере, даже если WEB уже показывал правильный editor.
        // Это защищает endpoint от ручных HTTP-запросов с неправильным типом значения.
        if (!TryNormalizeWriteValue(request.Value, itemDefinition.Kind, out var writeValue, out var normalizeError))
            return WriteFailed(response, normalizeError);

        response.WrittenValue = writeValue;

        // Перед записью обязательно разрешаем реальный tag и читаем текущее значение:
        // оно нужно оператору, audit и диагностике.
        var tagName = await ResolveTagNameAsync(equipment.Name, itemDefinition.Name, ct);
        if (!IsReadableTagName(tagName))
            return WriteFailed(response, $"Tag for '{equipment.Name}.{itemDefinition.Name}' was not found.");

        response.TagName = tagName;

        string? currentValue;
        try
        {
            currentValue = await _nativeClient.TagReadAsync(tagName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Param write current value read failed. Equipment={Equipment}, Item={Item}, Tag={Tag}",
                equipment.Name,
                itemDefinition.Name,
                tagName);

            return WriteFailed(response, "Cannot read current value before Param write: " + ex.Message);
        }

        response.CurrentValue = currentValue;

        if (!options.Enabled)
            return WriteFailed(response, "Param writes are disabled. Set ParamWrites:Enabled = true to allow write requests.");

        // DryRun специально расположен после всех проверок и чтения current value:
        // так можно проверить allow-list/tag resolve без реальной записи в Plant SCADA.
        if (options.DryRun)
        {
            response.Success = true;
            response.DryRun = true;
            response.Message = "Dry-run: validation succeeded, CtApi TagWrite was not executed.";
            return response;
        }

        if (!_options.Value.AllowWrites)
            return WriteFailed(response, "CtApi writes are disabled. Set CtApi:AllowWrites = true to allow real writes.");

        try
        {
            await _nativeClient.TagWriteAsync(tagName, writeValue, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Param TagWrite failed. Equipment={Equipment}, Item={Item}, Tag={Tag}, Value={Value}",
                equipment.Name,
                itemDefinition.Name,
                tagName,
                writeValue);

            return WriteFailed(response, "CtApi TagWrite failed: " + ex.Message);
        }

        response.Success = true;
        response.DryRun = false;
        response.Message = "Param value was written.";

        if (options.AuditEnabled)
        {
            // Audit best-effort: если SaveActionOperators упадет, основная запись считается выполненной.
            response.AuditAttempted = true;
            response.AuditSucceeded = await TrySaveOperatorActionAsync(
                equipment,
                itemDefinition.Name,
                currentValue,
                writeValue,
                request.Comment,
                request.Actor,
                ct);
        }

        return response;
    }

    private async Task<ParamDiDoRefDto?> TryResolveDryRunDiAsync(
        string dryRunEquipmentName,
        IReadOnlyDictionary<string, EquipmentDto> catalogLookup,
        CancellationToken ct)
    {
        var winRef = await GetWinOpenedRefAsync(
            dryRunEquipmentName,
            equipmentItem: "DryRunA",
            assocExpected: "_dryRunDI",
            ct);

        if (winRef is null)
            return null;

        var linkedEquipment = FindEquipment(catalogLookup, winRef.Value.RefEquipment);
        if (linkedEquipment is null || linkedEquipment.TypeGroup != EquipmentTypeGroup.DI)
            return null;

        var snapshot = await GetSnapshotAsync(linkedEquipment, ct);
        return snapshot.Supported
            ? BuildDiDoRef(linkedEquipment, snapshot)
            : null;
    }

    private async Task<ParamLinkedParamDto?> TryResolveDryRunAiAsync(
        string dryRunEquipmentName,
        IReadOnlyDictionary<string, EquipmentDto> catalogLookup,
        CancellationToken ct)
    {
        var winRef = await GetWinOpenedRefAsync(
            dryRunEquipmentName,
            equipmentItem: "DryRunA",
            assocExpected: "_dryRunAI",
            ct);

        if (winRef is null)
            return null;

        var linkedEquipment = FindEquipment(catalogLookup, winRef.Value.RefEquipment);
        if (linkedEquipment is null || linkedEquipment.TypeGroup != EquipmentTypeGroup.AI)
            return null;

        var snapshot = await GetSnapshotAsync(linkedEquipment, ct);
        return snapshot.Supported
            ? BuildLinkedParam(linkedEquipment, snapshot)
            : null;
    }

    private async Task<EquipmentRef?> GetWinOpenedRefAsync(
        string equipmentName,
        string equipmentItem,
        string assocExpected,
        CancellationToken ct)
    {
        var refs = await BrowseEquipmentRefsAsync(
            equipmentName,
            category: "WinOpened",
            equipmentItem: equipmentItem,
            fields: ["REFEQUIP", "ASSOC", "REFITEM"],
            ct);

        foreach (var row in refs)
        {
            var refEquipment = CleanRefValue(GetValue(row, "REFEQUIP"));
            if (string.IsNullOrWhiteSpace(refEquipment))
                continue;

            var assoc = CleanRefValue(GetValue(row, "ASSOC"));
            if (!string.Equals(assoc, assocExpected, StringComparison.OrdinalIgnoreCase))
                continue;

            var refItem = CleanRefValue(GetValue(row, "REFITEM"));
            return new EquipmentRef(refEquipment, assoc, refItem);
        }

        return null;
    }

    private async Task<ParamLinkedParamDto> ReadLinkedItemsAsync(
        EquipmentDto? equipment,
        string equipmentName,
        IReadOnlyList<ParamItemDefinition> definitions,
        CancellationToken ct)
    {
        var dto = new ParamLinkedParamDto
        {
            EquipmentName = equipmentName,
            TypeName = equipment?.TypeName ?? "",
            TypeGroup = equipment?.TypeGroup ?? EquipmentTypeGroup.Unknown,
            Description = equipment?.Description,
            Location = equipment?.Location
        };

        var tags = new List<(ParamItemDefinition Definition, string TagName)>();

        foreach (var definition in definitions)
        {
            ct.ThrowIfCancellationRequested();

            var tagName = await ResolveTagNameAsync(equipmentName, definition.Name, ct);
            if (IsReadableTagName(tagName))
                tags.Add((definition, tagName));
        }

        var rawMap = await TagReadManyAsync(
            tags
                .Select(x => x.TagName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ct);

        foreach (var (definition, tagName) in tags)
        {
            if (!rawMap.TryGetValue(tagName, out var raw) || raw is null)
                continue;

            var item = BuildItemDto(definition, tagName, raw);
            item.CanWrite = equipment is not null
                && ParamWriteDefinitions.CanWrite(equipment.TypeGroup, definition.Name);

            if (definition.Kind == ParamValueKind.Number)
            {
                var unit = await ResolveUnitAsync(tagName, ct);
                item.Unit = unit;

                if (string.IsNullOrWhiteSpace(dto.Unit) && !string.IsNullOrWhiteSpace(unit))
                    dto.Unit = unit;
            }

            dto.Items.Add(item);
        }

        return dto;
    }

    private async Task<List<Dictionary<string, string>>> BrowseEquipmentRefsAsync(
        string equipmentName,
        string category,
        string equipmentItem,
        IReadOnlyList<string> fields,
        CancellationToken ct)
    {
        var result = new List<Dictionary<string, string>>();
        var cluster = await ResolveClusterWithFallbackAsync(equipmentName, equipmentItem, ct);

        if (string.IsNullOrWhiteSpace(cluster)
            || cluster.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var connect = $"CLUSTER={cluster};EQUIP={equipmentName};REFCAT={category}";
        var escapedConnect = EscapeCicodeString(connect);

        var handleText = (await _nativeClient.CicodeAsync(
            $"EquipRefBrowseOpen(\"{escapedConnect}\",\"\")",
            ct) ?? "").Trim();

        if (!int.TryParse(handleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle)
            || handle == -1)
        {
            return result;
        }

        try
        {
            var countText = (await _nativeClient.CicodeAsync(
                $"EquipRefBrowseNumRecords({handle})",
                ct) ?? "").Trim();

            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                || count <= 0)
            {
                return result;
            }

            var nextText = (await _nativeClient.CicodeAsync(
                $"EquipRefBrowseFirst({handle})",
                ct) ?? "").Trim();

            while (int.TryParse(nextText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next)
                && next == 0)
            {
                ct.ThrowIfCancellationRequested();

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var field in fields)
                {
                    var escapedField = EscapeCicodeString(field);
                    row[field] = (await _nativeClient.CicodeAsync(
                        $"EquipRefBrowseGetField(\"{handle}\", \"{escapedField}\")",
                        ct) ?? "").Trim();
                }

                result.Add(row);

                nextText = (await _nativeClient.CicodeAsync(
                    $"EquipRefBrowseNext({handle})",
                    ct) ?? "").Trim();
            }
        }
        finally
        {
            try
            {
                await _nativeClient.CicodeAsync($"EquipRefBrowseClose({handle})", ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "EquipRefBrowseClose failed. Equipment={Equipment}, Category={Category}",
                    equipmentName,
                    category);
            }
        }

        return result;
    }

    private async Task<string> ResolveClusterWithFallbackAsync(
        string equipmentName,
        string preferredItem,
        CancellationToken ct)
    {
        foreach (var item in new[] { preferredItem, "STW", "State", "Value", "R" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cluster = await ResolveClusterAsync(equipmentName, item, ct);

            if (!string.IsNullOrWhiteSpace(cluster)
                && !cluster.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return cluster;
            }
        }

        return "";
    }

    private async Task<Dictionary<string, string?>> TagReadManyAsync(
        IReadOnlyList<string> tagNames,
        CancellationToken ct)
    {
        var maxConcurrency = Math.Max(1, _options.Value.TagReadParallelism);
        var result = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = tagNames.Select(async tagName =>
        {
            await gate.WaitAsync(ct);
            try
            {
                result[tagName] = await _nativeClient.TagReadAsync(tagName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Param reference TagRead failed. Tag={Tag}", tagName);
                result[tagName] = null;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return new Dictionary<string, string?>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static ParamDiDoRefDto BuildDiDoRef(
        EquipmentDto equipment,
        ParamSnapshotResponse snapshot)
    {
        var value = FindItem(snapshot, "Value");
        var valueForced = FindItem(snapshot, "ValueForced");
        var forceCmd = FindItem(snapshot, "ForceCmd");

        return new ParamDiDoRefDto
        {
            EquipmentName = equipment.Name,
            TypeName = equipment.TypeName,
            TypeGroup = equipment.TypeGroup,
            Description = equipment.Description,
            Location = equipment.Location,
            Chanel = string.IsNullOrWhiteSpace(equipment.Location)
                ? snapshot.Location
                : equipment.Location,
            ValueText = value?.ValueText,
            NumericValue = value?.NumericValue,
            BooleanValue = value?.BooleanValue,
            ValueForced = valueForced?.BooleanValue == true,
            ForceCmd = forceCmd?.BooleanValue == true
        };
    }

    private static ParamLinkedParamDto BuildLinkedParam(
        EquipmentDto equipment,
        ParamSnapshotResponse snapshot)
    {
        return new ParamLinkedParamDto
        {
            EquipmentName = equipment.Name,
            TypeName = equipment.TypeName,
            TypeGroup = equipment.TypeGroup,
            Description = equipment.Description,
            Location = string.IsNullOrWhiteSpace(equipment.Location)
                ? snapshot.Location
                : equipment.Location,
            Unit = snapshot.Unit,
            Items = snapshot.Items.ToList()
        };
    }

    private static Dictionary<string, EquipmentDto> BuildEquipmentLookup(
        IReadOnlyList<EquipmentDto> equipmentCatalog)
    {
        return equipmentCatalog
            .Where(item => !item.IsGroup)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.IsEquipmentChildNode ? 1 : 0)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static EquipmentDto? FindEquipment(
        IReadOnlyDictionary<string, EquipmentDto> equipmentCatalog,
        string equipmentName)
    {
        return equipmentCatalog.TryGetValue(equipmentName, out var equipment)
            ? equipment
            : null;
    }

    private static ParamItemDto? FindItem(
        ParamSnapshotResponse snapshot,
        string name)
    {
        return snapshot.Items.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyValue(ParamPlcRefDto row, string raw)
    {
        raw = (raw ?? "").Trim();
        row.ValueText = raw;

        if (IsBooleanPlcType(row.Type) && TryParseBoolean(raw, out var boolValue))
        {
            row.BooleanValue = boolValue;
            row.NumericValue = boolValue ? 1 : 0;
            row.ValueText = boolValue ? "true" : "false";
            return;
        }

        if (TryParseDouble(raw, out var number))
        {
            row.NumericValue = number;
            row.ValueText = FormatNumber(number);
        }
    }

    private static bool IsBooleanPlcType(ParamPlcType type)
    {
        return type is ParamPlcType.EqCheck
            or ParamPlcType.EqCheckRW
            or ParamPlcType.EqCheckDisplay
            or ParamPlcType.EqButton
            or ParamPlcType.EqButtonUp
            or ParamPlcType.EqButtonDown
            or ParamPlcType.EqButtonMode
            or ParamPlcType.EqButtonStartStop
            or ParamPlcType.EqDigital
            or ParamPlcType.EqDigitalInOut
            or ParamPlcType.EqMotorStatus
            or ParamPlcType.EqValveStatus;
    }

    private static ParamPlcType ParsePlcType(string? value)
    {
        value = (value ?? "").Trim();

        return Enum.TryParse(value, ignoreCase: true, out ParamPlcType type)
            ? type
            : ParamPlcType.Unknown;
    }

    private static string GetPlcEquipItemForTagInfo(string? refItem, ParamPlcType type)
    {
        refItem = CleanRefValue(refItem);

        if (!string.IsNullOrWhiteSpace(refItem))
            return refItem;

        if (type is ParamPlcType.EqMotorStatus or ParamPlcType.EqValveStatus)
            return "State";

        return "Value";
    }

    private static bool IsReadableTagName(string? tagName)
    {
        return !string.IsNullOrWhiteSpace(tagName)
            && !tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanRefValue(string? value)
    {
        value = (value ?? "").Trim();

        return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? ""
            : value;
    }

    private static long GetChanelSortKey(ParamDiDoRefDto row)
    {
        var raw = (row.ChanelShort ?? "").Trim();

        if (raw.Length == 0)
            return long.MaxValue;

        var parts = raw.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static int ParsePart(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : int.MaxValue;
        }

        var a = parts.Length > 0 ? ParsePart(parts[0]) : int.MaxValue;
        var b = parts.Length > 1 ? ParsePart(parts[1]) : int.MaxValue;
        var c = parts.Length > 2 ? ParsePart(parts[2]) : 0;

        return (long)a * 1_000_000L + (long)b * 1_000L + c;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : value.Value.ToUniversalTime();
    }

    private async Task<string> ResolveTagNameAsync(string equipmentName, string itemName, CancellationToken ct)
    {
        var key = $"{equipmentName}.{itemName}";

        if (_tagNameCache.TryGetValue(key, out var cached))
            return cached;

        var escapedKey = EscapeCicodeString(key);
        var tagName = (await _nativeClient.CicodeAsync($"TagInfo(\"{escapedKey}\", 0)", ct) ?? "").Trim();

        _tagNameCache[key] = tagName;
        return tagName;
    }

    private async Task<string> ResolveUnitAsync(string tagName, CancellationToken ct)
    {
        tagName = (tagName ?? "").Trim();
        if (tagName.Length == 0 || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return "";

        if (_tagUnitCache.TryGetValue(tagName, out var cached))
            return cached;

        var escapedTagName = EscapeCicodeString(tagName);
        var unit = CleanScadaText(await _nativeClient.CicodeAsync($"TagInfo(\"{escapedTagName}\", 1)", ct));

        _tagUnitCache[tagName] = unit;
        return unit;
    }

    private async Task<TrendRef?> ResolveTrendNameAsync(string equipmentName, string itemName, CancellationToken ct)
    {
        var key = $"{equipmentName}.{itemName}";

        if (_trendNameCache.TryGetValue(key, out var cachedTrendName))
        {
            var cachedCluster = await ResolveClusterAsync(equipmentName, itemName, ct);
            return string.IsNullOrWhiteSpace(cachedTrendName)
                ? null
                : new TrendRef(cachedTrendName, cachedCluster);
        }

        var cluster = await ResolveClusterAsync(equipmentName, itemName, ct);
        if (string.IsNullOrWhiteSpace(cluster)
            || cluster.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            _trendNameCache[key] = "";
            return null;
        }

        var escapedCluster = EscapeCicodeString(cluster);
        var escapedEquipmentName = EscapeCicodeString(equipmentName);
        var escapedItemName = EscapeCicodeString(itemName);

        var trendName = (await _nativeClient.CicodeAsync(
            $"_SATrend_GetTrendTag(\"{escapedCluster}\", \"{escapedEquipmentName}\", \"{escapedItemName}\")",
            ct) ?? "").Trim();

        _trendNameCache[key] = trendName;

        if (string.IsNullOrWhiteSpace(trendName)
            || trendName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new TrendRef(trendName, cluster);
    }

    private async Task<string> ResolveClusterAsync(string equipmentName, string itemName, CancellationToken ct)
    {
        var escapedKey = EscapeCicodeString($"{equipmentName}.{itemName}");
        return (await _nativeClient.CicodeAsync($"TagInfo(\"{escapedKey}\", 17)", ct) ?? "").Trim();
    }

    private async Task<List<TrendRow>> QueryTrendRowsAsync(
        TrendRef trendRef,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        const float period = 1.0f;
        const int dataMode = 1;
        const int instantTrend = 0;
        // Match the WPF TrnQuery(start, end, ...) overload: Plant SCADA expects
        // 250 ms here even when period is 1 second.
        const int samplePeriod = 250;

        var end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var numSamples = Math.Max(1, (int)Math.Round((toUtc - fromUtc).TotalSeconds / period));

        var displayMode = global::CtApi.DisplayMode.Get(
            global::CtApi.Ordering.OldestToNewest,
            global::CtApi.Condense.Mean,
            global::CtApi.Stretch.Raw,
            0,
            global::CtApi.BadQuality.Zero,
            global::CtApi.Raw.None);

        var query = string.Join(
            ",",
            "TRNQUERY",
            end.ToString(CultureInfo.InvariantCulture),
            toUtc.Millisecond.ToString(CultureInfo.InvariantCulture),
            period.ToString(CitectNumberFormat),
            numSamples.ToString(CultureInfo.InvariantCulture),
            trendRef.TrendName,
            displayMode.ToString(CultureInfo.InvariantCulture),
            dataMode.ToString(CultureInfo.InvariantCulture),
            instantTrend.ToString(CultureInfo.InvariantCulture),
            samplePeriod.ToString(CultureInfo.InvariantCulture));

        var clusters = GetTrendQueryClusters(trendRef.Cluster);
        var rows = Array.Empty<Dictionary<string, string>>() as IReadOnlyList<Dictionary<string, string>>;

        foreach (var cluster in clusters)
        {
            try
            {
                rows = await _nativeClient.FindAsync(
                    query,
                    filter: null,
                    cluster: cluster,
                    properties: ["DATETIME", "MSECONDS", "VALUE", "QUALITY"],
                    ct);

                if (rows.Count > 0)
                    break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Param trend query failed. Trend={Trend}, Cluster={Cluster}",
                    trendRef.TrendName,
                    cluster);
            }
        }

        var result = new List<TrendRow>(rows.Count);

        foreach (var row in rows)
        {
            if (!long.TryParse(GetValue(row, "DATETIME"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                continue;

            if (!int.TryParse(GetValue(row, "MSECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
                milliseconds = 0;

            if (!TryParseDouble(GetValue(row, "VALUE"), out var value) || double.IsNaN(value))
                continue;

            if (!int.TryParse(GetValue(row, "QUALITY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality))
                quality = 0;

            var timeUtc = DateTimeOffset
                .FromUnixTimeSeconds(seconds)
                .AddMilliseconds(milliseconds)
                .UtcDateTime;

            result.Add(new TrendRow(timeUtc, value, quality));
        }

        return result;
    }

    private static IReadOnlyList<string> GetTrendQueryClusters(string? tagCluster)
    {
        var clusters = new List<string> { "Cluster1" };
        tagCluster = (tagCluster ?? "").Trim();

        if (tagCluster.Length > 0
            && !tagCluster.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !clusters.Contains(tagCluster, StringComparer.OrdinalIgnoreCase))
        {
            clusters.Add(tagCluster);
        }

        return clusters;
    }

    private static ParamItemDto BuildItemDto(ParamItemDefinition definition, string tagName, string? raw)
    {
        raw = (raw ?? "").Trim();

        var dto = new ParamItemDto
        {
            Name = definition.Name,
            Kind = definition.Kind,
            TagName = tagName,
            ValueText = raw
        };

        if (definition.Kind == ParamValueKind.Boolean)
        {
            if (TryParseBoolean(raw, out var boolValue))
            {
                dto.BooleanValue = boolValue;
                dto.NumericValue = boolValue ? 1 : 0;
                dto.ValueText = boolValue ? "true" : "false";
            }
        }
        else if (definition.Kind == ParamValueKind.Number)
        {
            if (TryParseDouble(raw, out var number))
            {
                dto.NumericValue = number;
                dto.ValueText = FormatNumber(number);
            }
        }

        return dto;
    }

    private static ParamWriteResponse WriteFailed(ParamWriteResponse response, string error)
    {
        response.Success = false;
        response.Error = error;
        response.Time = DateTime.Now;
        return response;
    }

    /// <summary>
    /// Приводит пользовательское значение к формату, который безопасно отдавать в CtApi TagWrite.
    /// Boolean пишется как 1/0, number - invariant string без лишнего double-хвоста.
    /// </summary>
    private static bool TryNormalizeWriteValue(
        string? raw,
        ParamValueKind kind,
        out string value,
        out string error)
    {
        value = "";
        error = "";

        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "Write value is required.";
            return false;
        }

        if (kind == ParamValueKind.Boolean)
        {
            if (TryParseWriteBoolean(raw, out var boolValue))
            {
                value = boolValue ? "1" : "0";
                return true;
            }

            error = "Boolean write value must be one of: 1, 0, true, false, on, off.";
            return false;
        }

        if (kind == ParamValueKind.Number)
        {
            var normalized = raw.Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && !double.IsNaN(number)
                && !double.IsInfinity(number))
            {
                value = FormatWriteNumber(number);
                return true;
            }

            error = "Number write value is invalid.";
            return false;
        }

        error = $"Write is not supported for value kind '{kind}'.";
        return false;
    }

    /// <summary>
    /// Разбирает boolean в вариантах, которые встречаются в UI и CtApi: 1/0, true/false, on/off.
    /// </summary>
    private static bool TryParseWriteBoolean(string raw, out bool value)
    {
        raw = (raw ?? "").Trim();

        if (raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (raw.Equals("0", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("off", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private async Task<bool> TrySaveOperatorActionAsync(
        EquipmentDto equipment,
        string itemName,
        string? currentValue,
        string? newValue,
        string? description,
        string? actor,
        CancellationToken ct)
    {
        try
        {
            // Повторяем WPF-поведение: сначала кладем заголовок оборудования в sWndTitle,
            // затем вызываем Cicode-функцию, которая сама пишет действие оператора в SCADA/Event DB.
            var safeName = ToCicodeStringArg($"{equipment.Name}.{itemName}");
            var safeCurrent = ToCicodeValueArg(currentValue);
            var safeNew = ToCicodeValueArg(newValue);
            var safeDescription = ToCicodeStringArg(description ?? itemName);
            var safeDeviceName = ToCicodeStringArg(actor);

            var equipmentDescription = GetEquipmentAuditTitle(equipment);
            await _nativeClient.TagWriteAsync(
                "sWndTitle",
                $"\"{EscapeCicodeString(equipmentDescription)}\"",
                ct);

            await _nativeClient.CicodeAsync(
                $"SaveActionOperators({safeName}, {safeCurrent}, {safeNew}, {safeDescription}, {safeDeviceName})",
                ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Param write operator action audit failed. Equipment={Equipment}, Item={Item}",
                equipment.Name,
                itemName);

            return false;
        }
    }

    private static string GetEquipmentAuditTitle(EquipmentDto equipment)
    {
        if (!string.IsNullOrWhiteSpace(equipment.Description))
            return equipment.Description;

        if (!string.IsNullOrWhiteSpace(equipment.DisplayName))
            return equipment.DisplayName;

        return equipment.Name;
    }

    /// <summary>
    /// Формирует строковый аргумент Cicode с экранированием двойных кавычек.
    /// </summary>
    private static string ToCicodeStringArg(string? value)
    {
        return $"\"{EscapeCicodeString((value ?? "").Trim())}\"";
    }

    /// <summary>
    /// Формирует Cicode-аргумент: числа и boolean передаются без кавычек, текст - как строка.
    /// Это важно для SaveActionOperators, который в SCADA ожидает смешанные типы.
    /// </summary>
    private static string ToCicodeValueArg(object? value)
    {
        if (value is null)
            return "\"\"";

        if (value is bool b)
            return b ? "1" : "0";

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";

        if (value is float or double or decimal)
            return Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(',', '.') ?? "0";

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";

        if (TryParseWriteBoolean(text, out var boolValue))
            return boolValue ? "1" : "0";

        var numeric = text.Replace(',', '.');
        if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return numeric;

        return ToCicodeStringArg(text);
    }

    private static ParamTrendItemDto ToDto(ParamTrendItemDefinition definition)
    {
        return new ParamTrendItemDto
        {
            Name = definition.Name,
            Color = definition.Color,
            NativeMin = definition.NativeMin,
            NativeMax = definition.NativeMax
        };
    }

    private static (double Min, double Max) ResolveBaseRange(
        ParamTrendItemDefinition baseItem,
        ParamSnapshotResponse snapshot)
    {
        if (baseItem.NativeMin.HasValue && baseItem.NativeMax.HasValue)
            return NormalizeRange(baseItem.NativeMin.Value, baseItem.NativeMax.Value);

        var minR = snapshot.Items.FirstOrDefault(x => x.Name.Equals("MinR", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var maxR = snapshot.Items.FirstOrDefault(x => x.Name.Equals("MaxR", StringComparison.OrdinalIgnoreCase))?.NumericValue;

        if (minR.HasValue && maxR.HasValue)
            return NormalizeRange(minR.Value, maxR.Value);

        return (0, 1);
    }

    private static (double Min, double Max) ResolveNativeRange(
        ParamTrendItemDefinition item,
        double baseMin,
        double baseMax)
    {
        if (item.NativeMin.HasValue && item.NativeMax.HasValue)
            return NormalizeRange(item.NativeMin.Value, item.NativeMax.Value);

        return NormalizeRange(baseMin, baseMax);
    }

    private static (double Min, double Max) NormalizeRange(double a, double b)
    {
        if (Math.Abs(a - b) < 1e-12)
            return (a, a + 1);

        return a < b ? (a, b) : (b, a);
    }

    private static double MapToBase(double raw, double fromMin, double fromMax, double baseMin, double baseMax)
    {
        var fromSpan = fromMax - fromMin;
        if (Math.Abs(fromSpan) < 1e-12)
            return baseMin;

        var t = (raw - fromMin) / fromSpan;
        t = Math.Clamp(t, 0, 1);

        return baseMin + t * (baseMax - baseMin);
    }

    private static bool TryParseBoolean(string raw, out bool value)
    {
        raw = (raw ?? "").Trim();

        if (raw == "1")
        {
            value = true;
            return true;
        }

        if (raw == "0")
        {
            value = false;
            return true;
        }

        return bool.TryParse(raw, out value);
    }

    private static bool TryParseDouble(string raw, out double value)
    {
        raw = (raw ?? "").Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Форматирует число именно для записи, сохраняя больше точности, чем обычный display-format.
    /// </summary>
    private static string FormatWriteNumber(double value)
    {
        return value.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string CleanScadaText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = value.Trim();

        if (text.StartsWith("@(", StringComparison.Ordinal)
            && text.EndsWith(")", StringComparison.Ordinal)
            && text.Length >= 3)
        {
            text = text[2..^1].Trim();

            if (text.Length >= 2
                && ((text[0] == '"' && text[^1] == '"')
                    || (text[0] == '\'' && text[^1] == '\'')))
            {
                text = text[1..^1].Trim();
            }
        }

        return text;
    }

    private static string EscapeCicodeString(string value)
    {
        return (value ?? "").Replace("\"", "\"\"");
    }

    private static string GetValue(Dictionary<string, string> row, string name)
    {
        return row.TryGetValue(name, out var value) ? value : "";
    }

    private readonly record struct TrendRef(string TrendName, string Cluster);

    private readonly record struct TrendRow(DateTime TimeUtc, double Value, int Quality);

    private readonly record struct EquipmentRef(string RefEquipment, string Assoc, string RefItem);
}

internal sealed record ParamDefinition(
    EquipmentTypeGroup TypeGroup,
    IReadOnlyList<ParamItemDefinition> Items,
    IReadOnlyList<ParamTrendItemDefinition> TrendItems,
    IReadOnlyList<ParamPageKind> Pages);

internal sealed record ParamItemDefinition(string Name, ParamValueKind Kind);

internal sealed record ParamTrendItemDefinition(
    string Name,
    string Color,
    double? NativeMin = null,
    double? NativeMax = null);

internal static class ParamDefinitions
{
    private static readonly Dictionary<EquipmentTypeGroup, ParamDefinition> Definitions = new()
    {
        [EquipmentTypeGroup.AI] = new(
            EquipmentTypeGroup.AI,
            [
                B("AlarmLAEn"), B("AlarmLWEn"), B("AlarmHWEn"), B("AlarmHAEn"), B("ForceCmd"),
                B("NotTripLow"), B("NotTripHigh"), B("RealVar"), B("AlarmLA"), B("AlarmLW"),
                B("AlarmHW"), B("AlarmHA"), B("AlarmA"), B("Shunt"), B("AlarmW"), B("AlarmHealth"),
                N("STW"), N("Min"), N("Max"), N("MinR"), N("MaxR"), N("Flt"), N("Coef"),
                N("Value"), N("Hmi"), N("HmiTrue"), N("HmiForced"), N("SetLA"), N("SetLW"),
                N("SetHW"), N("SetHA"), N("SetHyst"), N("R"), N("HashCode")
            ],
            [T("R", "#2E7D32")],
            []),

        [EquipmentTypeGroup.DI] = new(
            EquipmentTypeGroup.DI,
            [B("Value"), B("ValueTrue"), B("ValueForced"), B("ForceCmd"), B("AlarmHealth"), B("NotTrip"), B("Shunt"), N("STW"), N("HashCode")],
            [T("Value", "#4F81BD", 0, 1)],
            []),

        [EquipmentTypeGroup.DO] = new(
            EquipmentTypeGroup.DO,
            [B("Value"), B("ValueTrue"), B("ValueForced"), B("ForceCmd"), B("AlarmHealth"), B("Reset"), N("STW"), N("HashCode")],
            [T("Value", "#4F81BD", 0, 1)],
            []),

        [EquipmentTypeGroup.Motor] = new(
            EquipmentTypeGroup.Motor,
            [
                B("Mode"), B("Auto"), B("Man"), B("AlarmAEn"), B("AlarmA"), B("TimeWorkAlarmW"),
                B("TimeWorkAlarmWAck"), B("TimeReset"), B("On"), B("NotTrip"),
                N("STW"), N("State"), N("TimeWarn"), N("TimeSet"), N("TimeHmi"), N("TimeWork"), N("HashCode")
            ],
            [T("Man", "CornflowerBlue", 0, 1.1), T("Mode", "Green", 0, 1.5), T("AlarmA", "Red", 0, 2)],
            [ParamPageKind.Plc, ParamPageKind.DiDo, ParamPageKind.Alarm, ParamPageKind.TimeWork, ParamPageKind.DryRun, ParamPageKind.Atv]),

        [EquipmentTypeGroup.ATV] = new(
            EquipmentTypeGroup.ATV,
            [
                B("Mode"), B("AlarmLAEn"), B("AlarmLWEn"), B("AlarmHWEn"), B("AlarmHAEn"), B("ForceCmd"),
                B("AlarmLA"), B("AlarmLW"), B("AlarmHW"), B("AlarmHA"), B("AlarmA"), B("AlarmW"),
                B("AlarmHealth"), B("Run"), B("AlarmEn"), B("Alarm"), B("Start"), B("StopType"),
                N("STW01"), N("STW02"), N("OutMin"), N("OutMax"), N("NMax"), N("NHmi"), N("IHmi"),
                N("RpmHmi"), N("FHmi"), N("THmi"), N("IL1R"), N("Nsp"), N("Cli"), N("RemoteSet"),
                N("RemoteAcc"), N("RemoteDec"), N("LocalSet"), N("LocalAcc"), N("LocalDec"), N("Man"),
                N("ManTrue"), N("ManForced"), N("SetLA"), N("SetLW"), N("SetHW"), N("SetHA"), N("SetHyst"),
                N("R"), N("HashCode")
            ],
            [T("Man", "#4F81BD", 0, 100)],
            [ParamPageKind.Atv, ParamPageKind.Alarm]),

        [EquipmentTypeGroup.VGA] = new(
            EquipmentTypeGroup.VGA,
            [
                B("Mode"), B("AlarmLAEn"), B("AlarmLWEn"), B("AlarmHWEn"), B("AlarmHAEn"), B("ForceCmd"),
                B("AlarmLA"), B("AlarmLW"), B("AlarmHW"), B("AlarmHA"), B("AlarmA"), B("AlarmW"),
                B("AlarmHealth"), N("STW"), N("Min"), N("Max"), N("MinR"), N("MaxR"), N("OutMin"),
                N("OutMax"), N("Value"), N("Man"), N("ManTrue"), N("ManForced"), N("SetLA"), N("SetLW"),
                N("SetHW"), N("SetHA"), N("SetHyst"), N("R"), N("HashCode")
            ],
            [T("R", "#4F81BD")],
            []),

        [EquipmentTypeGroup.VGA_EL] = new(
            EquipmentTypeGroup.VGA_EL,
            [
                B("Mode"), B("OpenCmd"), B("CloseCmd"), B("AlarmEn"), B("Alarm"), B("SQEn"), B("ActuatorEn"),
                B("Opened"), B("Closed"), B("OpenAl"), B("CloseAl"), N("State"), N("Man"), N("CurrPos"),
                N("TimeOpening"), N("OutMin"), N("OutMax"), N("R"), N("STW"), N("HashCode")
            ],
            [T("CurrPos", "#808080", 0, 100), T("R", "#4F81BD", 0, 100)],
            [ParamPageKind.DiDo, ParamPageKind.Alarm]),

        [EquipmentTypeGroup.VGD] = new(
            EquipmentTypeGroup.VGD,
            [
                B("Mode"), B("Auto"), B("Man"), B("AlarmEn"), B("AlarmA"), B("AlarmOpen"), B("AlarmClose"),
                B("Opened"), B("Closed"), B("Dcs"), B("NotTrip"), N("STW"), N("State"), N("TOpen"),
                N("TClose"), N("HashCode")
            ],
            [T("Man", "CornflowerBlue", 0, 1.1), T("Mode", "Green", 0, 1.5), T("AlarmOpen", "Red", 0, 2), T("AlarmClose", "Orange", 0, 2.5)],
            [ParamPageKind.Plc, ParamPageKind.DiDo, ParamPageKind.Alarm])
    };

    public static bool TryGet(EquipmentTypeGroup typeGroup, out ParamDefinition definition)
    {
        return Definitions.TryGetValue(typeGroup, out definition!);
    }

    private static ParamItemDefinition B(string name) => new(name, ParamValueKind.Boolean);

    private static ParamItemDefinition N(string name) => new(name, ParamValueKind.Number);

    private static ParamTrendItemDefinition T(string name, string color, double? nativeMin = null, double? nativeMax = null) =>
        new(name, color, nativeMin, nativeMax);
}

/// <summary>
/// Описывает, какие EquipItem разрешены к записи для каждого типа оборудования.
/// Это второй рубеж защиты после UI: даже ручной HTTP-запрос не запишет item вне allow-list.
/// </summary>
internal static class ParamWriteDefinitions
{
    /// <summary>
    /// Список разрешенных к записи EquipItem по типам оборудования.
    /// Все отсутствующие здесь параметры считаются read-only независимо от действий WEB UI.
    /// </summary>
    private static readonly Dictionary<EquipmentTypeGroup, HashSet<string>> WritableItems = new()
    {
        [EquipmentTypeGroup.AI] = Names(
            "AlarmLAEn", "AlarmLWEn", "AlarmHWEn", "AlarmHAEn", "ForceCmd",
            "Min", "Max", "MinR", "MaxR", "Flt", "Coef", "SetLA", "SetLW",
            "SetHW", "SetHA", "SetHyst", "HmiForced"),

        [EquipmentTypeGroup.DI] = Names("ForceCmd", "ValueForced"),

        [EquipmentTypeGroup.DO] = Names("ForceCmd", "ValueForced"),

        [EquipmentTypeGroup.Motor] = Names(
            "Mode", "Man", "AlarmAEn", "TimeReset",
            "TimeWarn", "TimeSet"),

        [EquipmentTypeGroup.ATV] = Names(
            "Mode", "AlarmLAEn", "AlarmLWEn", "AlarmHWEn", "AlarmHAEn",
            "ForceCmd", "AlarmEn", "OutMin", "OutMax", "NMax", "Nsp", "Cli",
            "RemoteSet", "RemoteAcc", "RemoteDec", "LocalSet", "LocalAcc",
            "LocalDec", "Man", "ManForced", "SetLA", "SetLW", "SetHW",
            "SetHA", "SetHyst"),

        [EquipmentTypeGroup.VGA] = Names(
            "Mode", "AlarmLAEn", "AlarmLWEn", "AlarmHWEn", "AlarmHAEn",
            "ForceCmd", "Min", "Max", "MinR", "MaxR", "OutMin", "OutMax",
            "Man", "ManForced", "SetLA", "SetLW", "SetHW", "SetHA", "SetHyst"),

        [EquipmentTypeGroup.VGA_EL] = Names(
            "Mode", "OpenCmd", "CloseCmd", "AlarmEn", "SQEn", "ActuatorEn",
            "Man", "TimeOpening", "OutMin", "OutMax"),

        [EquipmentTypeGroup.VGD] = Names(
            "Mode", "Man", "AlarmEn", "TOpen", "TClose")
    };

    public static bool CanWrite(EquipmentTypeGroup typeGroup, string itemName)
    {
        return WritableItems.TryGetValue(typeGroup, out var names)
            && names.Contains(itemName);
    }

    private static HashSet<string> Names(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }
}
