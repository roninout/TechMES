using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using TechMES.Application.Soe;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Soe;
using TechMES.Infrastructure.CtApi.Native;

namespace TechMES.Infrastructure.CtApi.Gateways;

public sealed class CtApiEquipmentSoeProvider : IEquipmentSoeProvider
{
    private const int WindowMinutes = 30;

    private readonly ICtApiNativeClient _nativeClient;
    private readonly ILogger<CtApiEquipmentSoeProvider> _logger;

    public CtApiEquipmentSoeProvider(
        ICtApiNativeClient nativeClient,
        ILogger<CtApiEquipmentSoeProvider> logger)
    {
        _nativeClient = nativeClient;
        _logger = logger;
    }

    public async Task<SoeResponse> GetSoeAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        int perTrendMax = 1000,
        int totalMax = 100,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        perTrendMax = Math.Clamp(perTrendMax, 1, 5000);
        totalMax = Math.Clamp(totalMax, 1, 2000);

        var response = new SoeResponse
        {
            EquipmentName = equipment.Name,
            Supported = true,
            LoadedAt = DateTime.Now
        };

        var trendModels = await BuildTrendModelsAsync(equipment, equipmentCatalog, ct);
        trendModels = trendModels
            .Where(x => !string.IsNullOrWhiteSpace(x.TrendName))
            .GroupBy(x => x.TrendName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (trendModels.Count == 0)
        {
            response.Supported = false;
            response.Message = "No SOE trend tags were found for this equipment.";
            return response;
        }

        var rows = new List<SoeEventDto>(Math.Min(totalMax, 512));

        foreach (var model in trendModels)
        {
            ct.ThrowIfCancellationRequested();

            if (rows.Count >= totalMax)
                break;

            var localRows = await ReadTrendEventsAsync(
                model,
                Math.Min(perTrendMax, totalMax - rows.Count),
                ct);

            rows.AddRange(localRows);
        }

        response.Rows = rows
            .OrderByDescending(x => x.TimeUtc)
            .Take(totalMax)
            .ToList();
        response.TotalLoaded = response.Rows.Count;

        if (response.Rows.Count == 0)
            response.Message = "No SOE events were found from the beginning of the current day.";

        return response;
    }

    private async Task<List<SoeTrendModel>> BuildTrendModelsAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        CancellationToken ct)
    {
        var result = new List<SoeTrendModel>();
        var catalogLookup = equipmentCatalog
            .Where(x => !x.IsGroup)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(item => item.IsEquipmentChildNode ? 1 : 0).First(),
                StringComparer.OrdinalIgnoreCase);

        var mainItem = await ResolveSoeItemAsync(equipment, ct);
        result.Add(await BuildTrendModelAsync(equipment, mainItem, ct));

        var refNames = await BrowseRefEquipmentNamesAsync(
            equipment.Name,
            category: "TabDIDO",
            equipmentItem: mainItem,
            ct);

        foreach (var refName in refNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (!catalogLookup.TryGetValue(refName, out var refEquipment))
                continue;

            var refItem = await ResolveSoeItemAsync(refEquipment, ct);
            result.Add(await BuildTrendModelAsync(refEquipment, refItem, ct));
        }

        return result;
    }

    private async Task<SoeTrendModel> BuildTrendModelAsync(
        EquipmentDto equipment,
        string itemName,
        CancellationToken ct)
    {
        var trendRef = await ResolveTrendNameAsync(equipment.Name, itemName, ct);

        return new SoeTrendModel(
            equipment.Name,
            equipment.TypeName,
            equipment.TypeGroup,
            itemName,
            trendRef?.TrendName ?? "",
            trendRef?.Cluster ?? "");
    }

    private async Task<List<SoeEventDto>> ReadTrendEventsAsync(
        SoeTrendModel model,
        int maxRows,
        CancellationToken ct)
    {
        var result = new List<SoeEventDto>(maxRows);
        var dayStartUtc = DateTime.SpecifyKind(DateTime.Now.Date, DateTimeKind.Local).ToUniversalTime();
        var endUtc = DateTime.UtcNow;
        long? lastWord = null;

        while (result.Count < maxRows && endUtc > dayStartUtc)
        {
            ct.ThrowIfCancellationRequested();

            var startUtc = endUtc.AddMinutes(-WindowMinutes);
            if (startUtc < dayStartUtc)
                startUtc = dayStartUtc;

            var points = await QueryTrendRowsAsync(
                new TrendRef(model.TrendName, model.Cluster),
                startUtc,
                endUtc,
                ct);

            foreach (var point in points)
            {
                ct.ThrowIfCancellationRequested();

                if (point.QualityText.Equals("Bad", StringComparison.OrdinalIgnoreCase))
                    break;

                var currentWord = ToLongWord(point.Value);

                if (!lastWord.HasValue)
                {
                    lastWord = currentWord;
                    continue;
                }

                if (currentWord == lastWord.Value)
                    continue;

                var bitCode = GetChangedBitCode(
                    (ushort)(lastWord.Value & 0xFFFF),
                    (ushort)(currentWord & 0xFFFF));

                result.Add(new SoeEventDto
                {
                    TimeUtc = point.TimeUtc,
                    TypeGroup = model.TypeGroup,
                    Equipment = model.EquipmentName,
                    TrendValue = point.Value,
                    BitCode = bitCode,
                    Event = SoeEventMapper.GetEventText(model.TypeGroup, bitCode),
                    EventKey = SoeEventMapper.GetEventKey(model.TypeGroup, bitCode),
                    ValueQuality = point.QualityText
                });

                lastWord = currentWord;

                if (result.Count >= maxRows)
                    break;
            }

            endUtc = startUtc.AddMilliseconds(-1);
        }

        return result;
    }

    private async Task<string> ResolveSoeItemAsync(
        EquipmentDto equipment,
        CancellationToken ct)
    {
        if (equipment.TypeGroup == EquipmentTypeGroup.ATV)
        {
            var stw01TagName = await ResolveTagNameAsync(equipment.Name, "STW01", ct);
            if (IsReadableValue(stw01TagName))
                return "STW01";
        }

        return "STW";
    }

    private async Task<List<string>> BrowseRefEquipmentNamesAsync(
        string equipmentName,
        string category,
        string equipmentItem,
        CancellationToken ct)
    {
        var rows = await BrowseEquipmentRefsAsync(
            equipmentName,
            category,
            equipmentItem,
            ["REFEQUIP"],
            ct);

        return rows
            .Select(row => CleanRefValue(GetValue(row, "REFEQUIP")))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
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

        if (!IsReadableValue(cluster))
            return result;

        var connect = EscapeCicodeString($"CLUSTER={cluster};EQUIP={equipmentName};REFCAT={category}");
        var handleText = (await _nativeClient.CicodeAsync(
            $"EquipRefBrowseOpen(\"{connect}\",\"\")",
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
                    row[field] = (await _nativeClient.CicodeAsync(
                        $"EquipRefBrowseGetField(\"{handle}\", \"{EscapeCicodeString(field)}\")",
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
                _logger.LogDebug(ex, "SOE EquipRefBrowseClose failed. Equipment={Equipment}", equipmentName);
            }
        }

        return result;
    }

    private async Task<TrendRef?> ResolveTrendNameAsync(
        string equipmentName,
        string itemName,
        CancellationToken ct)
    {
        var cluster = await ResolveClusterAsync(equipmentName, itemName, ct);
        if (!IsReadableValue(cluster))
            return null;

        var trendName = (await _nativeClient.CicodeAsync(
            $"_SATrend_GetTrendTag(\"{EscapeCicodeString(cluster)}\", \"{EscapeCicodeString(equipmentName)}\", \"{EscapeCicodeString(itemName)}\")",
            ct) ?? "").Trim();

        return IsReadableValue(trendName)
            ? new TrendRef(trendName, cluster)
            : null;
    }

    private async Task<string> ResolveTagNameAsync(
        string equipmentName,
        string itemName,
        CancellationToken ct)
    {
        var key = EscapeCicodeString($"{equipmentName}.{itemName}");
        return (await _nativeClient.CicodeAsync($"TagInfo(\"{key}\", 0)", ct) ?? "").Trim();
    }

    private async Task<string> ResolveClusterAsync(
        string equipmentName,
        string itemName,
        CancellationToken ct)
    {
        var key = EscapeCicodeString($"{equipmentName}.{itemName}");
        return (await _nativeClient.CicodeAsync($"TagInfo(\"{key}\", 17)", ct) ?? "").Trim();
    }

    private async Task<string> ResolveClusterWithFallbackAsync(
        string equipmentName,
        string preferredItem,
        CancellationToken ct)
    {
        foreach (var item in new[] { preferredItem, "STW01", "STW", "State", "Value" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cluster = await ResolveClusterAsync(equipmentName, item, ct);
            if (IsReadableValue(cluster))
                return cluster;
        }

        return "";
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
            period.ToString(CultureInfo.InvariantCulture),
            numSamples.ToString(CultureInfo.InvariantCulture),
            trendRef.TrendName,
            displayMode.ToString(CultureInfo.InvariantCulture),
            dataMode.ToString(CultureInfo.InvariantCulture),
            instantTrend.ToString(CultureInfo.InvariantCulture),
            samplePeriod.ToString(CultureInfo.InvariantCulture));

        IReadOnlyList<Dictionary<string, string>> rows = [];
        foreach (var cluster in GetTrendQueryClusters(trendRef.Cluster))
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
                _logger.LogWarning(ex, "SOE trend query failed. Trend={Trend}, Cluster={Cluster}", trendRef.TrendName, cluster);
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

            var qualityText = GetValue(row, "QUALITY");
            var timeUtc = DateTimeOffset
                .FromUnixTimeSeconds(seconds)
                .AddMilliseconds(milliseconds)
                .UtcDateTime;

            result.Add(new TrendRow(timeUtc, value, qualityText));
        }

        return result;
    }

    private static IReadOnlyList<string> GetTrendQueryClusters(string? tagCluster)
    {
        var clusters = new List<string> { "Cluster1" };
        tagCluster = (tagCluster ?? "").Trim();

        if (IsReadableValue(tagCluster)
            && !clusters.Contains(tagCluster, StringComparer.OrdinalIgnoreCase))
        {
            clusters.Add(tagCluster);
        }

        return clusters;
    }

    private static int GetChangedBitCode(ushort last, ushort current)
    {
        var diff = (ushort)(current ^ last);
        if (diff == 0)
            return -99;

        var bitPos = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((diff & (1 << i)) != 0)
            {
                bitPos = i + 1;
                break;
            }
        }

        var nowSet = (current & (1 << (bitPos - 1))) != 0;
        return nowSet ? bitPos : bitPos + 16;
    }

    private static long ToLongWord(double value)
    {
        return Convert.ToInt64(Math.Truncate(value), CultureInfo.InvariantCulture);
    }

    private static bool IsReadableValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanRefValue(string? value)
    {
        value = (value ?? "").Trim();
        return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    private static bool TryParseDouble(string raw, out double value)
    {
        raw = (raw ?? "").Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string EscapeCicodeString(string value)
    {
        return (value ?? "").Replace("\"", "\"\"");
    }

    private static string GetValue(Dictionary<string, string> row, string name)
    {
        return row.TryGetValue(name, out var value) ? value : "";
    }

    private readonly record struct SoeTrendModel(
        string EquipmentName,
        string TypeName,
        EquipmentTypeGroup TypeGroup,
        string ItemName,
        string TrendName,
        string Cluster);

    private readonly record struct TrendRef(string TrendName, string Cluster);

    private readonly record struct TrendRow(DateTime TimeUtc, double Value, string QualityText);
}

internal static class SoeEventMapper
{
    public static string GetEventText(EquipmentTypeGroup typeGroup, int bitCode)
    {
        var field = GetEnumField(typeGroup, bitCode);
        var attr = field?.GetCustomAttribute<DescriptionAttribute>();
        return attr?.Description ?? GetEventKey(typeGroup, bitCode);
    }

    public static string GetEventKey(EquipmentTypeGroup typeGroup, int bitCode)
    {
        var field = GetEnumField(typeGroup, bitCode);
        return field?.Name ?? "";
    }

    private static FieldInfo? GetEnumField(EquipmentTypeGroup typeGroup, int bitCode)
    {
        if (bitCode <= 0)
            return null;

        var enumType = typeGroup switch
        {
            EquipmentTypeGroup.AI => typeof(AiSoeCode),
            EquipmentTypeGroup.VGA => typeof(AoSoeCode),
            EquipmentTypeGroup.DI => typeof(DiSoeCode),
            EquipmentTypeGroup.DO => typeof(DoSoeCode),
            EquipmentTypeGroup.ATV => typeof(AtvSoeCode),
            EquipmentTypeGroup.VGD => typeof(VgdSoeCode),
            EquipmentTypeGroup.Motor => typeof(MotorSoeCode),
            EquipmentTypeGroup.VGA_EL => typeof(VgaElSoeCode),
            _ => null
        };

        if (enumType is null || !Enum.IsDefined(enumType, bitCode))
            return null;

        var value = Enum.ToObject(enumType, bitCode);
        return enumType.GetField(value.ToString() ?? "");
    }

    private enum AiSoeCode
    {
        [Description("AL LA EN - Off")] AL_LA_EN_Off = 1,
        [Description("AL LW EN - Off")] AL_LW_EN_Off = 2,
        [Description("AL HW EN - Off")] AL_HW_EN_Off = 3,
        [Description("AL HA EN - Off")] AL_HA_EN_Off = 4,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 5,
        [Description("Reserve - Off")] Reserve_Off_22 = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("AL LA - Down")] AL_LA_Down = 9,
        [Description("AL LW - Down")] AL_LW_Down = 10,
        [Description("AL HW - Down")] AL_HW_Down = 11,
        [Description("AL HA - Down")] AL_HA_Down = 12,
        [Description("AL A - Down")] AL_A_Down = 13,
        [Description("AL W - Down")] AL_W_Down = 14,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,
        [Description("AL LA EN - On")] AL_LA_EN_On = 17,
        [Description("AL LW EN - On")] AL_LW_EN_On = 18,
        [Description("AL HW EN - On")] AL_HW_EN_On = 19,
        [Description("AL HA EN - On")] AL_HA_EN_On = 20,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 21,
        [Description("Reserve - On")] Reserve_On_06 = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("AL LA - Up")] AL_LA_Up = 25,
        [Description("AL LW - Up")] AL_LW_Up = 26,
        [Description("AL HW - Up")] AL_HW_Up = 27,
        [Description("AL HA - Up")] AL_HA_Up = 28,
        [Description("AL A - Up")] AL_A_Up = 29,
        [Description("AL W - Up")] AL_W_Up = 30,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32
    }

    private enum AoSoeCode
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("AL LA EN - Off")] AL_LA_EN_Off = 2,
        [Description("AL LW EN - Off")] AL_LW_EN_Off = 3,
        [Description("AL HW EN - Off")] AL_HW_EN_Off = 4,
        [Description("AL HA EN - Off")] AL_HA_EN_Off = 5,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("AL LA - Down")] AL_LA_Down = 9,
        [Description("AL LW - Down")] AL_LW_Down = 10,
        [Description("AL HW - Down")] AL_HW_Down = 11,
        [Description("AL HA - Down")] AL_HA_Down = 12,
        [Description("AL A - Down")] AL_A_Down = 13,
        [Description("AL W - Down")] AL_W_Down = 14,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,
        [Description("RUN - Off")] RUN_Off = 17,
        [Description("AL EN - Off")] AL_EN_Off = 18,
        [Description("AL - Down")] AL_Down = 19,
        [Description("START - Down")] START_Down = 20,
        [Description("STOP TYPE - Freewheel")] STOP_TYPE_Freewheel = 21,
        [Description("Reserve - Off")] Reserve_Off_54 = 22,
        [Description("Reserve - Off")] Reserve_Off_55 = 23,
        [Description("Reserve - Off")] Reserve_Off_56 = 24,
        [Description("Reserve - Off")] Reserve_Off_57 = 25,
        [Description("Reserve - Off")] Reserve_Off_58 = 26,
        [Description("Reserve - Off")] Reserve_Off_59 = 27,
        [Description("Reserve - Off")] Reserve_Off_60 = 28,
        [Description("Reserve - Off")] Reserve_Off_61 = 29,
        [Description("Reserve - Off")] Reserve_Off_62 = 30,
        [Description("Reserve - Off")] Reserve_Off_63 = 31,
        [Description("Reserve - Off")] Reserve_Off_64 = 32
    }

    private enum DiSoeCode
    {
        [Description("VALUE - Off")] VALUE_Off = 1,
        [Description("VALUE TRUE - Off")] VALUE_TRUE_Off = 2,
        [Description("VALUE FORCED - Off")] VALUE_FORCED_Off = 3,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 4,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 5,
        [Description("Reserve - Off")] Reserve_Off_22 = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("Reserve - Off")] Reserve_Off_25 = 9,
        [Description("Reserve - Off")] Reserve_Off_26 = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("Reserve - Off")] Reserve_Off_30 = 14,
        [Description("Reserve - Off")] Reserve_Off_31 = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,
        [Description("VALUE - On")] VALUE_On = 17,
        [Description("VALUE TRUE - On")] VALUE_TRUE_On = 18,
        [Description("VALUE FORCED - On")] VALUE_FORCED_On = 19,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 20,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 21,
        [Description("Reserve - On")] Reserve_On_06 = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("Reserve - On")] Reserve_On_09 = 25,
        [Description("Reserve - On")] Reserve_On_10 = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("Reserve - On")] Reserve_On_14 = 30,
        [Description("Reserve - On")] Reserve_On_15 = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32
    }

    private enum DoSoeCode
    {
        [Description("VALUE TRUE - Off")] VALUE_TRUE_Off = 1,
        [Description("VALUE - Off")] VALUE_Off = 2,
        [Description("VALUE FORCED - Off")] VALUE_FORCED_Off = 3,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 4,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 5,
        [Description("Reserve - Off")] Reserve_Off_22 = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("Reserve - Off")] Reserve_Off_25 = 9,
        [Description("Reserve - Off")] Reserve_Off_26 = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("Reserve - Off")] Reserve_Off_30 = 14,
        [Description("Reserve - Off")] Reserve_Off_31 = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,
        [Description("VALUE TRUE - On")] VALUE_TRUE_On = 17,
        [Description("VALUE - On")] VALUE_On = 18,
        [Description("VALUE FORCED - On")] VALUE_FORCED_On = 19,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 20,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 21,
        [Description("Reserve - On")] Reserve_On_06 = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("Reserve - On")] Reserve_On_09 = 25,
        [Description("Reserve - On")] Reserve_On_10 = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("Reserve - On")] Reserve_On_14 = 30,
        [Description("Reserve - On")] Reserve_On_15 = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32
    }

    private enum AtvSoeCode
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("AL LA EN - Off")] AL_LA_EN_Off = 2,
        [Description("AL LW EN - Off")] AL_LW_EN_Off = 3,
        [Description("AL HW EN - Off")] AL_HW_EN_Off = 4,
        [Description("AL HA EN - Off")] AL_HA_EN_Off = 5,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 6,
        [Description("Reserve - Off")] Reserve_Off_39 = 7,
        [Description("Reserve - Off")] Reserve_Off_40 = 8,
        [Description("AL LA - Down")] AL_LA_Down = 9,
        [Description("AL LW - Down")] AL_LW_Down = 10,
        [Description("AL HW - Down")] AL_HW_Down = 11,
        [Description("AL HA - Down")] AL_HA_Down = 12,
        [Description("AL A - Down")] AL_A_Down = 13,
        [Description("AL W - Down")] AL_W_Down = 14,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 15,
        [Description("Reserve - Off")] Reserve_Off_48 = 16,
        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("AL LA EN - On")] AL_LA_EN_On = 18,
        [Description("AL LW EN - On")] AL_LW_EN_On = 19,
        [Description("AL HW EN - On")] AL_HW_EN_On = 20,
        [Description("AL HA EN - On")] AL_HA_EN_On = 21,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("AL LA - Up")] AL_LA_Up = 25,
        [Description("AL LW - Up")] AL_LW_Up = 26,
        [Description("AL HW - Up")] AL_HW_Up = 27,
        [Description("AL HA - Up")] AL_HA_Up = 28,
        [Description("AL A - Up")] AL_A_Up = 29,
        [Description("AL W - Up")] AL_W_Up = 30,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32
    }

    private enum VgdSoeCode
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("CMD - Off")] CMD_Off = 2,
        [Description("MAN - Off")] MAN_Off = 3,
        [Description("AL_EN - Off")] AL_EN_Off = 4,
        [Description("AL - Off")] AL_Off = 5,
        [Description("OPEN_AL - Off")] OPEN_AL_Off = 6,
        [Description("CLOSE_AL - Off")] CLOSE_AL_Off = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("OPENED - Off")] OPENED_Off = 9,
        [Description("CLOSED - Off")] CLOSED_Off = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("NOT_TRIP_DCS - Off")] NOT_TRIP_DCS_Off = 14,
        [Description("EMER_OFF - Off")] EMER_OFF_Off = 15,
        [Description("NOT_TRIP_EDS - Off")] NOT_TRIP_EDS_Off = 16,
        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("CMD - On")] CMD_On = 18,
        [Description("MAN - On")] MAN_On = 19,
        [Description("AL_EN - On")] AL_EN_On = 20,
        [Description("AL - On")] AL_On = 21,
        [Description("OPEN_AL - On")] OPEN_AL_On = 22,
        [Description("CLOSE_AL - On")] CLOSE_AL_On = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("OPENED - On")] OPENED_On = 25,
        [Description("CLOSED - On")] CLOSED_On = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("NOT_TRIP_DCS - On")] NOT_TRIP_DCS_On = 30,
        [Description("EMER_OFF - On")] EMER_OFF_On = 31,
        [Description("NOT_TRIP_EDS - On")] NOT_TRIP_EDS_On = 32
    }

    private enum MotorSoeCode
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("CMD - Off")] CMD_Off = 2,
        [Description("MAN - Off")] MAN_Off = 3,
        [Description("AL_EN - Off")] AL_EN_Off = 4,
        [Description("AL - Off")] AL_Off = 5,
        [Description("T_WORK_AL - Off")] T_WORK_AL_Off = 6,
        [Description("T_WORK_AL_ACK - Off")] T_WORK_AL_ACK_Off = 7,
        [Description("TIME_RESET - Off")] TIME_RESET_Off = 8,
        [Description("Reserve - Off")] Reserve_Off_25 = 9,
        [Description("Reserve - Off")] Reserve_Off_26 = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("READY - Off")] READY_Off = 14,
        [Description("EMER_OFF - Off")] EMER_OFF_Off = 15,
        [Description("NOT_TRIP_EDS - Off")] NOT_TRIP_EDS_Off = 16,
        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("CMD - On")] CMD_On = 18,
        [Description("MAN - On")] MAN_On = 19,
        [Description("AL_EN - On")] AL_EN_On = 20,
        [Description("AL - On")] AL_On = 21,
        [Description("T_WORK_AL - On")] T_WORK_AL_On = 22,
        [Description("T_WORK_AL_ACK - On")] T_WORK_AL_ACK_On = 23,
        [Description("TIME_RESET - On")] TIME_RESET_On = 24,
        [Description("Reserve - On")] Reserve_On_09 = 25,
        [Description("Reserve - On")] Reserve_On_10 = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("READY - On")] READY_On = 30,
        [Description("EMER_OFF - On")] EMER_OFF_On = 31,
        [Description("NOT_TRIP_EDS - On")] NOT_TRIP_EDS_On = 32
    }

    private enum VgaElSoeCode
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("OPEN_CMD - Off")] OPEN_CMD_Off = 2,
        [Description("CLOSE_CMD - Off")] CLOSE_CMD_Off = 3,
        [Description("AL_EN - Off")] AL_EN_Off = 4,
        [Description("AL - Off")] AL_Off = 5,
        [Description("OPEN AL - Off")] OPEN_AL_Off = 6,
        [Description("CLOSE AL - Off")] CLOSE_AL_Off = 7,
        [Description("RESERVE - Off")] RESERVE_Off_24 = 8,
        [Description("OPENED - Off")] OPENED_Off = 9,
        [Description("CLOSED - Off")] CLOSED_Off = 10,
        [Description("RESERVE - Off")] RESERVE_Off_27 = 11,
        [Description("SQ_EN - Off")] SQ_EN_Off = 12,
        [Description("ACTUATOR EN - Off")] ACTUATOR_EN_Off = 13,
        [Description("RESERVE - Off")] RESERVE_Off_30 = 14,
        [Description("RESERVE - Off")] RESERVE_Off_31 = 15,
        [Description("RESERVE - Off")] RESERVE_Off_32 = 16,
        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("OPEN_CMD - On")] OPEN_CMD_On = 18,
        [Description("CLOSE_CMD - On")] CLOSE_CMD_On = 19,
        [Description("AL_EN - On")] AL_EN_On = 20,
        [Description("AL - On")] AL_On = 21,
        [Description("OPEN AL - On")] OPEN_AL_On = 22,
        [Description("CLOSE AL - On")] CLOSE_AL_On = 23,
        [Description("RESERVE - On")] RESERVE_On_08 = 24,
        [Description("OPENED - On")] OPENED_On = 25,
        [Description("CLOSED - On")] CLOSED_On = 26,
        [Description("RESERVE - On")] RESERVE_On_11 = 27,
        [Description("SQ_EN - On")] SQ_EN_On = 28,
        [Description("ACTUATOR EN - On")] ACTUATOR_EN_On = 29,
        [Description("RESERVE - On")] RESERVE_On_14 = 30,
        [Description("RESERVE - On")] RESERVE_On_15 = 31,
        [Description("RESERVE - On")] RESERVE_On_16 = 32
    }
}
