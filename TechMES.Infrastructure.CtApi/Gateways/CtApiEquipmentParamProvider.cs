using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TechMES.Application.Param;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Infrastructure.CtApi.Native;

namespace TechMES.Infrastructure.CtApi.Gateways;

public sealed class CtApiEquipmentParamProvider : IEquipmentParamProvider
{
    private static readonly CultureInfo CitectNumberFormat = CultureInfo.InvariantCulture;

    private readonly ICtApiNativeClient _nativeClient;
    private readonly ILogger<CtApiEquipmentParamProvider> _logger;

    private readonly ConcurrentDictionary<string, string> _tagNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _tagUnitCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _trendNameCache = new(StringComparer.OrdinalIgnoreCase);

    public CtApiEquipmentParamProvider(
        ICtApiNativeClient nativeClient,
        ILogger<CtApiEquipmentParamProvider> logger)
    {
        _nativeClient = nativeClient;
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
            [T("Value", "CornflowerBlue", -0.2, 1.2)],
            []),

        [EquipmentTypeGroup.DO] = new(
            EquipmentTypeGroup.DO,
            [B("Value"), B("ValueTrue"), B("ValueForced"), B("ForceCmd"), B("AlarmHealth"), B("Reset"), N("STW"), N("HashCode")],
            [T("Value", "Blue", -0.2, 1.2)],
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
            [T("R", "#4F81BD", 0, 100), T("CurrPos", "Gray", 0, 100)],
            [ParamPageKind.Plc, ParamPageKind.DiDo, ParamPageKind.Alarm]),

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
