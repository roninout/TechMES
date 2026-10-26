using System.Globalization;
using TechMES.Application.Scada;
using TechMES.Contracts.Recipes;
using TechMES.Contracts.Scada;

namespace TechMES.Application.Recipes;

/// <summary>План строится только из сохранённых данных под блокировками БД.</summary>
public sealed class RecipePlcUpload
{
    // В TecxRecipeAX цикл i < 50 очищал именно позиции 1–49.
    public const int LastStageNumber = 49;

    public RecipePageDto Page { get; }
    public bool WriteAttempted { get; private set; }

    private readonly Dictionary<string, double> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _parameters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _booleanTags = new(StringComparer.OrdinalIgnoreCase);

    public RecipePlcUpload(RecipeSnapshotDto snapshot, RecipePageDto page, IReadOnlyList<RecipeColumnDto> familyParameters, IReadOnlyDictionary<int, string> stageCodes, IReadOnlyList<string> unitPrefixes)
    {
        Page = page;

        if (string.IsNullOrWhiteSpace(page.ModeTag) || snapshot.Stages.Count == 0)
            throw new InvalidOperationException("Configure the Unit mode tag and select a recipe with stages.");

        var stages = snapshot.Stages.OrderBy(stage => stage.Number).ToArray();
        var prefixes = unitPrefixes.Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (prefixes.Count == 0 || prefixes.Any(value => !ValidAddressPart(value)))
            throw new InvalidOperationException("The Unit's Current recipe has no valid PLC prefixes.");

        if (!stages.Select(stage => stage.Number).SequenceEqual(Enumerable.Range(1, stages.Length)) || stages.Length > LastStageNumber)
            throw new InvalidOperationException($"Recipe stages must be numbered consecutively from 1 to {LastStageNumber}.");

        var definitions = familyParameters.Where(column => !string.IsNullOrWhiteSpace(column.TagSuffix)).ToDictionary(column => column.Id);

        if (definitions.Values.Any(column => !ValidAddressPart(column.TagSuffix.Trim()) || column.TagSuffix.Trim().Equals("NUM", StringComparison.OrdinalIgnoreCase) || !Enum.IsDefined(column.Kind)) || definitions.Values.GroupBy(column => column.TagSuffix.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Family parameter tags must be valid and unique; NUM is reserved.");

        // Отключаем также хвост предыдущего рецепта; каждый prefix очищаем один раз.
        foreach (var prefix in prefixes.Order(StringComparer.OrdinalIgnoreCase))
        {
            for (var number = 1; number <= LastStageNumber; number++)
                _numbers.Add($"{prefix}_{number}_NUM", 0);
        }

        foreach (var stage in stages)
        {
            var prefix = stage.TagPrefix.Trim();

            if (!prefixes.Contains(prefix))
                throw new InvalidOperationException($"Stage {stage.Number}: PLC prefix does not belong to the selected Unit.");

            if (!stageCodes.TryGetValue(stage.BaseStageId, out var text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 999)
                throw new InvalidOperationException($"Stage {stage.Number}: template code must be an integer from 0 to 999.");

            var address = $"{prefix}_{stage.Number.ToString(CultureInfo.InvariantCulture)}";
            _codes.Add($"{address}_NUM", code);

            foreach (var column in definitions.Values)
            {
                _parameters.Add($"{address}_{column.TagSuffix.Trim()}", 0);

                if (column.Kind == RecipeParameterKind.CheckBox)
                    _booleanTags.Add($"{address}_{column.TagSuffix.Trim()}");
            }

            foreach (var (id, parameter) in stage.Parameters)
            {
                if (!definitions.TryGetValue(id, out var column) || !double.IsFinite(parameter.Value) || (column.Kind == RecipeParameterKind.CheckBox && parameter.Value is not (0 or 1)))
                    throw new InvalidOperationException($"Stage {stage.Number}: invalid parameter [{id}] or PLC value.");

                // Суффиксы используем буквально. Старое отдельное поле Stage.Auto не записываем.
                _values.Add($"{address}_{column.TagSuffix.Trim()}", parameter.Value);
            }
        }

        if (_numbers.Keys.Concat(_parameters.Keys).Any(tag => tag.Equals(page.ModeTag.Trim(), StringComparison.OrdinalIgnoreCase) || tag.Equals(page.CurrentStageTag.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A recipe address conflicts with a Unit control tag.");
    }

    /// <summary>Проверяет адреса, загружает БД-значения и контролирует итоговое состояние PLC.</summary>
    public async Task ExecuteAsync(IPlantScadaGateway gateway, CancellationToken ct)
    {
        var expected = new Dictionary<string, double>(_numbers, StringComparer.OrdinalIgnoreCase);

        foreach (var item in _parameters) expected[item.Key] = item.Value;
        foreach (var item in _values) expected[item.Key] = item.Value;
        foreach (var item in _codes) expected[item.Key] = item.Value;

        // До первого TagWrite проверяем режим и доступность всех адресов.
        await CheckModeAsync(gateway, ct);
        await CheckReadingsAsync(gateway, expected, false, ct);
        await CheckModeAsync(gateway, ct);

        // NUM включаем последним, когда параметры уже записаны.
        await WriteAsync(gateway, _numbers, ct);
        await WriteAsync(gateway, _parameters, ct);
        await WriteAsync(gateway, _values, ct);
        await WriteAsync(gateway, _codes, ct);

        await Task.Delay(200, ct);
        await CheckReadingsAsync(gateway, expected, true, ct);
        await CheckModeAsync(gateway, ct);
    }

    private async Task WriteAsync(IPlantScadaGateway gateway, Dictionary<string, double> values, CancellationToken ct)
    {
        var count = 0;

        foreach (var (tag, value) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (count++ % 25 == 0)
                await CheckModeAsync(gateway, ct);

            ct.ThrowIfCancellationRequested();
            WriteAttempted = true;

            var result = await gateway.WriteTagAsync(new() { TagName = tag, Value = value.ToString("G17", CultureInfo.InvariantCulture) }, ct);

            if (!result.Success)
                throw new InvalidOperationException($"PLC write was not confirmed: {tag}. {result.Error}");
        }
    }

    private async Task CheckModeAsync(IPlantScadaGateway gateway, CancellationToken ct)
    {
        var error = await RecipePlcReader.ReadManualModeErrorAsync(Page, gateway, ct);

        if (error is not null)
            throw new InvalidOperationException(error);
    }

    private async Task CheckReadingsAsync(IPlantScadaGateway gateway, Dictionary<string, double> expected, bool compare, CancellationToken ct)
    {
        // Повторяем только контрольное чтение; TagWrite автоматически не повторяется.
        var pending = expected;

        for (var attempt = 0; attempt < (compare ? 3 : 1); attempt++)
        {
            if (attempt > 0)
                await Task.Delay(250, ct);

            var batch = await gateway.ReadTagsAsync(pending.Keys.ToArray(), ct);
            var readings = batch.Items.ToDictionary(item => item.TagName, StringComparer.OrdinalIgnoreCase);
            var failed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var (tag, value) in pending)
            {
                var kind = _booleanTags.Contains(tag) ? RecipeParameterKind.CheckBox : RecipeParameterKind.InputField;

                if (!readings.TryGetValue(tag, out var item) || !item.Success || item.Quality is ScadaTagQuality.Bad or ScadaTagQuality.Uncertain || !RecipePlcReader.TryReadValue(item.Value, kind, out var actual) || (compare && Math.Abs(actual - value) > Math.Max(1e-9, Math.Abs(value) * 1e-7)))
                    failed.Add(tag, value);
            }

            if (failed.Count == 0) return;

            pending = failed;
        }

        throw new InvalidOperationException($"PLC {(compare ? "verification" : "preflight reading")} failed for {pending.Count} tag(s). First: {pending.Keys.First()}.");
    }

    private static bool ValidAddressPart(string value) => value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}