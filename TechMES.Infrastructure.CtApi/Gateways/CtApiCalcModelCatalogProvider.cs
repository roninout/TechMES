using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechMES.Application.Calc;
using TechMES.Application.Equipment;
using TechMES.Contracts.Calc;
using TechMES.Infrastructure.CtApi.Native;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Загружает из Plant SCADA calculation models:
/// Tank, Density, Capacity и Content.
///
/// Подход повторяет существующий Equipment Catalog:
/// 1. через Tag table находим кандидатов;
/// 2. реальный Equipment Type обязательно проверяем через EquipGetProperty;
/// 3. в cache сохраняем только подтверждённые Calc Types.
///
/// В отличие от обычного Equipment Catalog этот provider НЕ загружается
/// автоматически при старте Runtime.Service.
/// </summary>
public sealed class CtApiCalcModelCatalogProvider(ICtApiNativeClient nativeClient, IOptions<EquipmentCatalogOptions> equipmentOptions, ILogger<CtApiCalcModelCatalogProvider> logger) : ICalcModelCatalogProvider
{
    private const string TagTableName = "Tag";
    private const string EquipmentField = "EQUIPMENT";
    private const string TagField = "TAG";
    private const string CommentField = "COMMENT";

    /*
     * Это только discovery filters.
     *
     * Тип модели НЕ определяется из имени тега.
     * После обнаружения Equipment мы обязательно читаем
     * EquipGetProperty(..., "Type", 3).
     */
    private static readonly string[] DiscoveryFilters =
    [
        "Tag=*_TANK_*",
        "Tag=*_DENS_*",
        "Tag=*_CAP_*",
        "Tag=*_CONT_*"
    ];

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private List<CalcModelDto> _cache = [];
    private DateTimeOffset? _lastLoadedAtUtc;
    private bool _isLoaded;

    /// <summary>
    /// Возвращает только уже загруженный cache.
    /// CtApi scan здесь никогда не запускается.
    /// </summary>
    public Task<CalcModelCatalogResponse> GetSnapshotAsync(CancellationToken ct = default)
    {
        return Task.FromResult(BuildResponse());
    }

    /// <summary>
    /// Полностью перестраивает calculation catalog.
    /// </summary>
    public async Task<CalcModelCatalogResponse> ReloadAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);

        try
        {
            var candidates = await FindCandidatesAsync(ct);

            logger.LogInformation(
                "Calc SCADA catalog refresh started. CandidateEquipmentCount={CandidateCount}.",
                candidates.Count);

            var result = new List<CalcModelDto>();

            foreach (var candidate in candidates.Values.OrderBy(item => item.Equipment, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                var scadaType = await GetEquipmentTypeAsync(candidate.Equipment, ct);

                if (!TryMapType(scadaType, out var type))
                    continue;

                var description = await GetEquipmentCommentAsync(
                    candidate.Equipment,
                    candidate.FallbackDescription,
                    ct);

                result.Add(new CalcModelDto
                {
                    Name = candidate.Equipment,
                    Description = description,
                    Station = ExtractStation(candidate.Equipment),
                    Type = type,
                    TagNames = candidate.TagNames.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList()
                });
            }

            _cache = result
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Station, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Type)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _lastLoadedAtUtc = DateTimeOffset.UtcNow;
            _isLoaded = true;

            logger.LogInformation(
                "Calc SCADA catalog refreshed. Total={Total}, Tank={TankCount}, Density={DensityCount}, Capacity={CapacityCount}, Content={ContentCount}.",
                _cache.Count,
                _cache.Count(item => item.Type == CalcModelTypeDto.Tank),
                _cache.Count(item => item.Type == CalcModelTypeDto.Density),
                _cache.Count(item => item.Type == CalcModelTypeDto.Capacity),
                _cache.Count(item => item.Type == CalcModelTypeDto.Content));

            return BuildResponse();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Ищет Equipment-кандидатов по известным tag naming conventions.
    ///
    /// Помимо Equipment сохраняем все реальные TAG-и найденной модели.
    /// Они уже получены текущим CtApi scan, поэтому отдельный lookup
    /// для Tank/Density bindings потом не требуется.
    /// </summary>
    private async Task<Dictionary<string, CalcModelCandidate>> FindCandidatesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, CalcModelCandidate>(StringComparer.OrdinalIgnoreCase);
        var cluster = string.IsNullOrWhiteSpace(equipmentOptions.Value.CtApiCluster) ? null : equipmentOptions.Value.CtApiCluster;

        foreach (var filter in DiscoveryFilters)
        {
            ct.ThrowIfCancellationRequested();

            var rows = await nativeClient.FindAsync(TagTableName, filter, cluster, [EquipmentField, TagField, CommentField], ct);

            foreach (var row in rows)
            {
                var equipment = GetValue(row, EquipmentField).Trim();
                var tag = GetValue(row, TagField).Trim();

                if (equipment.Length == 0)
                    continue;

                var comment = CleanScadaText(GetValue(row, CommentField));

                if (!result.TryGetValue(equipment, out var candidate))
                {
                    candidate = new CalcModelCandidate(equipment, comment);
                    result[equipment] = candidate;
                }
                else if (candidate.FallbackDescription.Length == 0 && comment.Length > 0)
                {
                    candidate.FallbackDescription = comment;
                }

                if (tag.Length > 0)
                    candidate.TagNames.Add(tag);
            }
        }

        return result;
    }

    /// <summary>
    /// Читает реальный Equipment Type тем же способом,
    /// который используется существующим Equipment Catalog.
    /// </summary>
    private async Task<string> GetEquipmentTypeAsync(string equipmentName, CancellationToken ct)
    {
        var equipment = EscapeCicodeString(equipmentName);

        var value = await nativeClient.CicodeAsync(
            $"EquipGetProperty(\"{equipment}\",\"Type\", 3)",
            ct);

        return (value ?? "").Trim();
    }

    /// <summary>
    /// Пытается прочитать COMMENT самого Equipment.
    /// Если конкретная версия Plant SCADA не отдаёт это свойство,
    /// используем COMMENT найденного Tag как fallback.
    /// </summary>
    private async Task<string> GetEquipmentCommentAsync(string equipmentName, string fallback, CancellationToken ct)
    {
        try
        {
            var equipment = EscapeCicodeString(equipmentName);

            var value = await nativeClient.CicodeAsync(
                $"EquipGetProperty(\"{equipment}\",\"Comment\", 3)",
                ct);

            var description = CleanScadaText(value);

            if (description.Length > 0
                && !string.Equals(description, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return description;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Cannot read Calc Equipment COMMENT. Equipment={Equipment}. Tag COMMENT fallback will be used.",
                equipmentName);
        }

        return fallback;
    }

    private CalcModelCatalogResponse BuildResponse()
    {
        var items = _cache
            .Select(CloneModel)
            .ToList();

        return new CalcModelCatalogResponse
        {
            IsAvailable = true,
            IsLoaded = _isLoaded,
            LoadedAtUtc = _lastLoadedAtUtc,
            TotalCount = items.Count,
            Items = items,
            Stations = items
                .Select(item => item.Station)
                .Where(station => !string.IsNullOrWhiteSpace(station))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(station => station, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Types = items
                .Select(item => item.Type)
                .Distinct()
                .OrderBy(type => type)
                .ToList()
        };
    }

    private static CalcModelDto CloneModel(CalcModelDto source)
    {
        return new CalcModelDto
        {
            Name = source.Name,
            Description = source.Description,
            Station = source.Station,
            Type = source.Type,
            TagNames = source.TagNames.ToList()
        };
    }

    /// <summary>
    /// Преобразует реальные Equipment Types Plant SCADA
    /// в четыре верхнеуровневых Calc Types WEB.
    ///
    /// Несколько SCADA Equipment Types могут использовать
    /// один Calc Type, если их расчётная структура одинакова.
    /// </summary>
    private static bool TryMapType(string scadaType, out CalcModelTypeDto type)
    {
        var normalized = NormalizeType(scadaType);

        switch (normalized)
        {
            case "TANK":
                type = CalcModelTypeDto.Tank;
                return true;

            case "DENSITY":
            case "DENSITYCICODE":
                type = CalcModelTypeDto.Density;
                return true;

            case "CAPACITY":
                type = CalcModelTypeDto.Capacity;
                return true;

            case "CONTENT":
                type = CalcModelTypeDto.Content;
                return true;

            default:
                type = default;
                return false;
        }
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
            (value ?? "")
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
    }

    private static string GetValue(Dictionary<string, string> row, string fieldName)
    {
        if (row.TryGetValue(fieldName, out var value))
            return value ?? "";

        var pair = row.FirstOrDefault(item =>
            string.Equals(item.Key, fieldName, StringComparison.OrdinalIgnoreCase));

        return pair.Value ?? "";
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

    private sealed class CalcModelCandidate
    {
        public CalcModelCandidate(string equipment, string fallbackDescription)
        {
            Equipment = equipment;
            FallbackDescription = fallbackDescription;
        }

        public string Equipment { get; }

        public string FallbackDescription { get; set; }


        // Пока оставляем для обратной совместимости. После переключения всех потребителей удалим.
        public HashSet<string> TagNames { get; } = new(StringComparer.OrdinalIgnoreCase);


        // ============================================================
        // Реальная структура Equipment:
        //
        // ITEM -> TAG.
        //
        // ITEM является частью фиксированного Equipment Type,
        // поэтому именно его используем как идентификатор параметра.
        // ============================================================
        public Dictionary<string, string> ItemTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}