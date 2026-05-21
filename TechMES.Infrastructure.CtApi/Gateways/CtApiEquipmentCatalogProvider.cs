using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Application.Equipment;
using TechMES.Contracts.Equipment;
using TechMES.Infrastructure.CtApi.Native;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Provider каталога оборудования через CtApi.
///
/// Важно:
/// старый WPF-проект не читал тип оборудования из таблицы EQUIP.
/// Там список строился через таблицу Tag:
/// - Tag=*_HASHCODE — обычное оборудование;
/// - Tag=*_EQUIP — group nodes;
/// а реальный тип читался отдельным Cicode-вызовом:
/// EquipGetProperty("S01.H01.M01", "Type", 3).
///
/// Поэтому здесь повторяем тот же путь, иначе CtFind по EQUIP может вернуть имена,
/// но не вернуть корректное поле TYPE, и весь список уйдёт в TypeGroup=Unknown.
/// </summary>
public sealed class CtApiEquipmentCatalogProvider : IEquipmentCatalogProvider
{
    private const string TagTableName = "Tag";
    private const string HashCodeTagFilter = "Tag=*_HASHCODE";
    private const string GroupTagFilter = "Tag=*_EQUIP";

    private const string EquipmentField = "EQUIPMENT";
    private const string TagField = "TAG";
    private const string CommentField = "COMMENT";

    private readonly ICtApiNativeClient _nativeClient;
    private readonly IOptions<EquipmentCatalogOptions> _options;
    private readonly ILogger<CtApiEquipmentCatalogProvider> _logger;

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private List<EquipmentDto> _cache = [];
    private DateTime _lastLoadedAt = DateTime.MinValue;

    public CtApiEquipmentCatalogProvider(
        ICtApiNativeClient nativeClient,
        IOptions<EquipmentCatalogOptions> options,
        ILogger<CtApiEquipmentCatalogProvider> logger)
    {
        _nativeClient = nativeClient;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        /*
            При старте Runtime.Service пробуем загрузить список оборудования.
            Если загрузка упадёт, Runtime.Service не должен падать:
            WEB сможет показать пустой список/ошибку, а пользователь увидит проблему.
        */
        try
        {
            await ReloadAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось загрузить список оборудования через CtApi при старте Runtime.Service.");
        }
    }

    public async Task<EquipmentListResponse> GetEquipmentListAsync(CancellationToken ct = default)
    {
        /*
            Пока делаем простой cache.
            Позже добавим кнопку force reload или период обновления.
        */
        if (_cache.Count == 0)
        {
            await ReloadAsync(ct);
        }

        var items = _cache
            .OrderBy(x => x.Station)
            .ThenBy(x => x.TypeGroup)
            .ThenBy(x => x.Name)
            .ToList();

        var stations = items
            .Select(x => x.Station)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        var typeGroups = items
            .Select(x => x.TypeGroup)
            .Where(x => x != EquipmentTypeGroup.Unknown)
            .Distinct()
            .OrderBy(x => x.ToString())
            .ToList();

        return new EquipmentListResponse
        {
            Equipments = items,
            Stations = stations,
            TypeGroups = typeGroups,
            TotalCount = items.Count
        };
    }

    public async Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default)
    {
        if (_cache.Count == 0)
        {
            await ReloadAsync(ct);
        }

        return _cache.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<EquipmentDto?> SetFavoriteAsync(string name, bool isFavorite, CancellationToken ct = default)
    {
        if (_cache.Count == 0)
        {
            await ReloadAsync(ct);
        }

        var items = _cache
            .Where(x => !x.IsGroup && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var item in items)
        {
            item.IsFavorite = isFavorite;
        }

        _logger.LogInformation(
            "Favorite flag changed in Runtime cache. Equipment={Equipment}, IsFavorite={IsFavorite}",
            name,
            isFavorite);

        return items.FirstOrDefault();
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct);

        try
        {
            var options = _options.Value;
            var scadaAllowedTypes = BuildScadaAllowedTypes(options);

            _logger.LogInformation(
                "Загружаем Equipment catalog через WPF-compatible CtApi flow. TagTable={TagTable}, HashFilter={HashFilter}, GroupFilter={GroupFilter}, ScadaAllowedTypes={ScadaAllowedTypes}",
                TagTableName,
                HashCodeTagFilter,
                GroupTagFilter,
                string.Join(",", scadaAllowedTypes));

            var findHashTags = await _nativeClient.FindAsync(
                TagTableName,
                HashCodeTagFilter,
                string.IsNullOrWhiteSpace(options.CtApiCluster) ? null : options.CtApiCluster,
                [EquipmentField, TagField, CommentField],
                ct);

            var findEquipTags = await _nativeClient.FindAsync(
                TagTableName,
                GroupTagFilter,
                string.IsNullOrWhiteSpace(options.CtApiCluster) ? null : options.CtApiCluster,
                [EquipmentField, TagField, CommentField],
                ct);

            var sourceEquipments = ExtractTagRows(findHashTags)
                .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Description))
                    .First())
                .OrderBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sourceGroups = ExtractTagRows(findEquipTags)
                .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Description))
                    .First())
                .OrderBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var plainEquipments = new List<EquipmentDto>(sourceEquipments.Count);
            var skippedByType = 0;
            var unknownTypeCount = 0;
            var nextPlainId = 1;

            foreach (var source in sourceEquipments)
            {
                ct.ThrowIfCancellationRequested();

                var scadaTypeName = await GetEquipmentTypeAsync(source.Equipment, ct);

                if (string.IsNullOrWhiteSpace(scadaTypeName)
                    || string.Equals(scadaTypeName, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    unknownTypeCount++;
                    continue;
                }

                if (!scadaAllowedTypes.Contains(scadaTypeName))
                {
                    skippedByType++;
                    continue;
                }

                var typeGroup = MapTypeGroup(scadaTypeName, options);

                if (typeGroup == EquipmentTypeGroup.Unknown)
                {
                    unknownTypeCount++;
                    continue;
                }

                var location = await GetEquipmentLocationAsync(source.Equipment, ct);

                plainEquipments.Add(new EquipmentDto
                {
                    Name = source.Equipment,
                    DisplayName = string.IsNullOrWhiteSpace(source.Description) ? source.Equipment : source.Description,
                    Description = source.Description,
                    Location = location,
                    Station = ExtractStation(source.Equipment),
                    TypeName = scadaTypeName,
                    TypeGroup = typeGroup,
                    IsGroup = false,
                    ParentName = null,
                    IsFavorite = false,
                    NodeId = $"EQ:{nextPlainId++}",
                    ParentNodeId = "0",
                    IsEquipmentChildNode = false
                });
            }

            var plainLookup = plainEquipments
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<EquipmentDto>(plainEquipments.Count + sourceGroups.Count);
            result.AddRange(plainEquipments);

            var nextGroupId = 1;

            foreach (var sourceGroup in sourceGroups)
            {
                ct.ThrowIfCancellationRequested();

                var groupNodeId = $"GRP:{nextGroupId++}";

                result.Add(new EquipmentDto
                {
                    Name = sourceGroup.Equipment,
                    DisplayName = string.IsNullOrWhiteSpace(sourceGroup.Description) ? sourceGroup.Equipment : sourceGroup.Description,
                    Description = sourceGroup.Description,
                    Location = "",
                    Station = ExtractStation(sourceGroup.Equipment),
                    TypeName = "Equipment",
                    TypeGroup = EquipmentTypeGroup.Equipment,
                    IsGroup = true,
                    ParentName = null,
                    IsFavorite = false,
                    NodeId = groupNodeId,
                    ParentNodeId = "0",
                    IsEquipmentChildNode = false
                });

                var childRefs = await GetEquipmentGroupRefsAsync(sourceGroup.Equipment, ct);
                var childIndex = 1;

                foreach (var childName in childRefs
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();

                    if (!plainLookup.TryGetValue(childName, out var childSource))
                        continue;

                    /*
                        Это отдельный tree-node на то же физическое оборудование.
                        Name остаётся тем же, но NodeId/ParentNodeId позволяют WEB позже
                        построить дерево так же, как WPF TreeListControl.
                    */
                    result.Add(new EquipmentDto
                    {
                        Name = childSource.Name,
                        DisplayName = childSource.DisplayName,
                        Description = childSource.Description,
                        Location = childSource.Location,
                        Station = childSource.Station,
                        TypeName = childSource.TypeName,
                        TypeGroup = childSource.TypeGroup,
                        IsGroup = false,
                        ParentName = sourceGroup.Equipment,
                        IsFavorite = childSource.IsFavorite,
                        NodeId = $"CH:{groupNodeId}:{childIndex++}",
                        ParentNodeId = groupNodeId,
                        IsEquipmentChildNode = true
                    });
                }
            }

            _cache = result
                .OrderBy(x => x.Station)
                .ThenBy(x => x.TypeGroup)
                .ThenBy(x => x.Name)
                .ToList();

            _lastLoadedAt = DateTime.Now;

            if (unknownTypeCount > 0)
            {
                _logger.LogWarning(
                    "Equipment catalog: {UnknownTypeCount} item(s) не получили корректный тип через EquipGetProperty.",
                    unknownTypeCount);
            }

            if (skippedByType > 0)
            {
                _logger.LogInformation(
                    "Equipment catalog: {SkippedByType} item(s) пропущены, потому что их SCADA Type не входит в ScadaAllowedTypes.",
                    skippedByType);
            }

            _logger.LogInformation(
                "Equipment catalog загружен через CtApi. HashTagRows={HashTagRows}, GroupTagRows={GroupTagRows}, PlainCount={PlainCount}, GroupCount={GroupCount}, TotalNodes={TotalNodes}",
                findHashTags.Count,
                findEquipTags.Count,
                plainEquipments.Count,
                sourceGroups.Count,
                _cache.Count);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<string> GetEquipmentTypeAsync(string equipmentName, CancellationToken ct)
    {
        var escapedEquipmentName = EscapeCicodeString(equipmentName);
        var value = await _nativeClient.CicodeAsync(
            $"EquipGetProperty(\"{escapedEquipmentName}\",\"Type\", 3)",
            ct);

        return (value ?? "").Trim();
    }

    private async Task<string> GetEquipmentLocationAsync(string equipmentName, CancellationToken ct)
    {
        try
        {
            var escapedEquipmentName = EscapeCicodeString(equipmentName);
            var value = await _nativeClient.CicodeAsync(
                $"EquipGetProperty(\"{escapedEquipmentName}\",\"Custom1\", 3)",
                ct);

            return (value ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    private async Task<List<string>> GetEquipmentGroupRefsAsync(string equipmentName, CancellationToken ct)
    {
        var result = new List<string>();

        var escapedEquipmentName = EscapeCicodeString(equipmentName);

        var cluster = (await _nativeClient.CicodeAsync(
            $"TagInfo(\"{escapedEquipmentName}.Value\", 17)",
            ct) ?? "").Trim();

        if (string.IsNullOrWhiteSpace(cluster) || string.Equals(cluster, "Unknown", StringComparison.OrdinalIgnoreCase))
            return result;

        var connect = $"CLUSTER={cluster};EQUIP={equipmentName};REFCAT=EquipGroup";
        var escapedConnect = EscapeCicodeString(connect);

        var handleText = (await _nativeClient.CicodeAsync(
            $"EquipRefBrowseOpen(\"{escapedConnect}\",\"REFEQUIP\")",
            ct) ?? "").Trim();

        if (!int.TryParse(handleText, out var handle) || handle == -1)
            return result;

        try
        {
            var countText = (await _nativeClient.CicodeAsync(
                $"EquipRefBrowseNumRecords({handle})",
                ct) ?? "").Trim();

            if (!int.TryParse(countText, out var count) || count <= 0)
                return result;

            var nextText = (await _nativeClient.CicodeAsync(
                $"EquipRefBrowseFirst({handle})",
                ct) ?? "").Trim();

            while (int.TryParse(nextText, out var next) && next == 0)
            {
                ct.ThrowIfCancellationRequested();

                var refEquip = (await _nativeClient.CicodeAsync(
                    $"EquipRefBrowseGetField(\"{handle}\", \"REFEQUIP\")",
                    ct) ?? "").Trim();

                if (!string.IsNullOrWhiteSpace(refEquip)
                    && !string.Equals(refEquip, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(refEquip);
                }

                nextText = (await _nativeClient.CicodeAsync(
                    $"EquipRefBrowseNext({handle})",
                    ct) ?? "").Trim();
            }
        }
        finally
        {
            await _nativeClient.CicodeAsync($"EquipRefBrowseClose({handle})", ct);
        }

        return result;
    }

    private static IEnumerable<SourceEquipment> ExtractTagRows(
        IEnumerable<Dictionary<string, string>> rows)
    {
        foreach (var row in rows)
        {
            var equipment = GetValue(row, EquipmentField).Trim();
            var tag = GetValue(row, TagField).Trim();
            var comment = GetValue(row, CommentField).Trim();

            if (string.IsNullOrWhiteSpace(equipment) || string.IsNullOrWhiteSpace(tag))
                continue;

            yield return new SourceEquipment(equipment, tag, comment);
        }
    }

    private static HashSet<string> BuildScadaAllowedTypes(
        EquipmentCatalogOptions options)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in options.ScadaAllowedTypes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value.Trim());
        }

        /*
            Если настройка пустая, используем WPF-набор типов по умолчанию.
        */
        if (result.Count == 0)
        {
            result.Add("Equipment");

            result.Add("DigitalIn");
            result.Add("DigitalInSiemens");

            result.Add("DigitalOut");
            result.Add("DigitalOutSiemens");

            result.Add("Motor");
            result.Add("MotorSiemens");

            result.Add("AnalogIn");
            result.Add("AnalogInSiemens");
            result.Add("AnalogInCalc");
            result.Add("AnalogInCalcSiemens");

            result.Add("ValveA");
            result.Add("ValveASiemens");
            result.Add("ValveA_EL");

            result.Add("ValveD");
            result.Add("ValveDSiemens");

            result.Add("Atv");
            result.Add("AtvSiemens");
        }

        return result;
    }

    private static EquipmentTypeGroup MapTypeGroup(
        string scadaTypeName,
        EquipmentCatalogOptions options)
    {
        if (string.IsNullOrWhiteSpace(scadaTypeName))
            return EquipmentTypeGroup.Unknown;

        var raw = scadaTypeName.Trim();

        /*
            Сначала aliases из appsettings.json.
            Именно здесь происходит:
                DigitalIn -> DI
                AnalogIn -> AI
                ValveA   -> VGA
        */
        if (options.TypeAliases is not null
            && options.TypeAliases.TryGetValue(raw, out var mappedValue)
            && Enum.TryParse<EquipmentTypeGroup>(mappedValue, ignoreCase: true, out var mappedType))
        {
            return mappedType;
        }

        var normalized = NormalizeType(raw);

        return normalized switch
        {
            "EQUIPMENT" => EquipmentTypeGroup.Equipment,

            "DIGITALIN" => EquipmentTypeGroup.DI,
            "DIGITALINSIEMENS" => EquipmentTypeGroup.DI,

            "DIGITALOUT" => EquipmentTypeGroup.DO,
            "DIGITALOUTSIEMENS" => EquipmentTypeGroup.DO,

            "MOTOR" => EquipmentTypeGroup.Motor,
            "MOTORSIEMENS" => EquipmentTypeGroup.Motor,

            "ANALOGIN" => EquipmentTypeGroup.AI,
            "ANALOGINSIEMENS" => EquipmentTypeGroup.AI,
            "ANALOGINCALC" => EquipmentTypeGroup.AI,
            "ANALOGINCALCSIEMENS" => EquipmentTypeGroup.AI,

            "VALVEA" => EquipmentTypeGroup.VGA,
            "VALVEASIEMENS" => EquipmentTypeGroup.VGA,

            "VALVEAEL" => EquipmentTypeGroup.VGA_EL,

            "VALVED" => EquipmentTypeGroup.VGD,
            "VALVEDSIEMENS" => EquipmentTypeGroup.VGD,

            "ATV" => EquipmentTypeGroup.ATV,
            "ATVSIEMENS" => EquipmentTypeGroup.ATV,

            _ => EquipmentTypeGroup.Unknown
        };
    }

    private static string GetValue(Dictionary<string, string> row, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return "";

        if (row.TryGetValue(fieldName, out var value))
            return value ?? "";

        /*
            На всякий случай делаем case-insensitive поиск,
            потому что разные CtApi/таблицы могут возвращать имена полей
            в разном регистре.
        */
        var pair = row.FirstOrDefault(x =>
            string.Equals(x.Key, fieldName, StringComparison.OrdinalIgnoreCase));

        return pair.Value ?? "";
    }

    private static string ExtractStation(string equipmentName)
    {
        if (string.IsNullOrWhiteSpace(equipmentName))
            return "";

        var dotIndex = equipmentName.IndexOf('.');

        return dotIndex > 0
            ? equipmentName[..dotIndex]
            : "";
    }

    private static string NormalizeType(string value)
    {
        return new string(
            value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
    }

    private static string EscapeCicodeString(string value)
    {
        /*
            В Cicode строке двойная кавычка экранируется удвоением.
            Для имён оборудования это почти никогда не нужно, но helper делает вызовы безопаснее.
        */
        return (value ?? "").Replace("\"", "\"\"");
    }

    private sealed record SourceEquipment(
        string Equipment,
        string Tag,
        string Description);
}
