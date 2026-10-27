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

        // Справочник может содержать новые и ещё не используемые параметры.
        // Для загрузки нужны только определения параметров выбранного рецепта.
        var usedParameterIds = stages.SelectMany(stage => stage.Parameters.Keys).ToHashSet();
        var definitions = familyParameters.Where(column => usedParameterIds.Contains(column.Id)).ToDictionary(column => column.Id);

        if (definitions.Count != usedParameterIds.Count)
            throw new InvalidOperationException("A recipe parameter is missing from its family. Refresh the recipe before loading PLC.");

        if (definitions.Values.Any(column => string.IsNullOrWhiteSpace(column.TagSuffix) || !ValidAddressPart(column.TagSuffix.Trim()) || column.TagSuffix.Trim().Equals("NUM", StringComparison.OrdinalIgnoreCase) || !Enum.IsDefined(column.Kind)) || definitions.Values.GroupBy(column => column.TagSuffix.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Used recipe parameter tags must be valid and unique; NUM is reserved.");

        // NUM — служебные теги: отключаем также хвост предыдущего рецепта.
        // Параметры неиспользуемых стадий при этом не читаем и не записываем.
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

            // Адрес создаём только для параметра, присутствующего в этой стадии.
            // Нельзя размножать все параметры семейства на каждую позицию рецепта.
            foreach (var (id, parameter) in stage.Parameters)
            {
                if (!definitions.TryGetValue(id, out var column) || !double.IsFinite(parameter.Value) || (column.Kind == RecipeParameterKind.CheckBox && parameter.Value is not (0 or 1)))
                    throw new InvalidOperationException($"Stage {stage.Number}: invalid parameter [{id}] or PLC value.");

                var tag = $"{address}_{column.TagSuffix.Trim()}";

                if (column.Kind == RecipeParameterKind.CheckBox)
                    _booleanTags.Add(tag);

                // Сохраняем существующую последовательность: обнуление, затем значение из БД.
                // Оба действия относятся только к параметрам конкретной стадии.
                _parameters.Add(tag, 0);
                _values.Add(tag, parameter.Value);
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

    /// <summary>Повторяет только неудачные чтения; команды записи здесь не выполняются.</summary>
    private async Task CheckReadingsAsync(IPlantScadaGateway gateway, Dictionary<string, double> expected, bool compare, CancellationToken ct)
    {
        // Перед записью даём CtApi до четырёх попыток получить корректные значения.
        // После записи сохраняем прежние три попытки проверки результата.
        var attempts = compare ? 3 : 4;
        var pending = expected;
        var firstError = "";

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                // Паузы перед повторным чтением: 500, 1000 и 1500 мс.
                // Проверка после записи использует прежнюю паузу 250 мс.
                var delay = compare ? 250 : attempt * 500;
                await Task.Delay(delay, ct);
            }

            var batch = await gateway.ReadTagsAsync(pending.Keys.ToArray(), ct);
            ct.ThrowIfCancellationRequested();

            var readings = batch.Items.ToDictionary(item => item.TagName, StringComparer.OrdinalIgnoreCase);
            var failed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            firstError = "";

            foreach (var (tag, value) in pending)
            {
                var kind = _booleanTags.Contains(tag) ? RecipeParameterKind.CheckBox : RecipeParameterKind.InputField;
                string? error = null;

                if (!readings.TryGetValue(tag, out var item))
                {
                    error = "PLC did not return this tag.";
                }
                else if (!item.Success || item.Quality is ScadaTagQuality.Bad or ScadaTagQuality.Uncertain)
                {
                    error = string.IsNullOrWhiteSpace(item.Error) ? $"PLC value is unavailable. Quality: {item.Quality}." : item.Error;
                }
                else if (!RecipePlcReader.TryReadValue(item.Value, kind, out var actual))
                {
                    error = $"PLC returned an invalid numeric or Boolean value: '{item.Value ?? "<null>"}'.";
                }
                else if (compare && Math.Abs(actual - value) > Math.Max(1e-9, Math.Abs(value) * 1e-7))
                {
                    error = $"Expected {value.ToString("G17", CultureInfo.InvariantCulture)}, received {actual.ToString("G17", CultureInfo.InvariantCulture)}.";
                }

                if (error is null) continue;

                failed.Add(tag, value);

                if (failed.Count == 1)
                    firstError = error;
            }

            if (failed.Count == 0) return;

            // Успешно прочитанные адреса повторно не запрашиваем.
            pending = failed;
        }

        var operation = compare ? "verification" : "preflight reading";

        throw new InvalidOperationException($"PLC {operation} failed after {attempts} attempts for {pending.Count} tag(s). First: {pending.Keys.First()}. Reason: {firstError}");
    }

    private static bool ValidAddressPart(string value) => value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}