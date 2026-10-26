using System.Globalization;
using TechMES.Application.Scada;
using TechMES.Contracts.Recipes;
using TechMES.Contracts.Scada;

namespace TechMES.Application.Recipes;

/// <summary>Читает параметры рецепта и CurrentStage одним логическим пакетом.</summary>
public static class RecipePlcReader
{
    public static async Task<RecipePlcSnapshotDto> ReadAsync(RecipeSnapshotDto snapshot, RecipePageDto page, IPlantScadaGateway gateway, CancellationToken ct = default)
    {
        var result = new RecipePlcSnapshotDto { RecipeId = snapshot.Recipe.Id, PageId = page.Id, CurrentRecipeId = page.RecipeId, LayoutStamp = RecipePlcLayout.GetStamp(snapshot) };
        var columns = snapshot.Columns.ToDictionary(column => column.Id);

        foreach (var stage in snapshot.Stages)
        {
            foreach (var columnId in stage.Parameters.Keys)
            {
                var item = new RecipePlcValueDto { StageId = stage.Id, ColumnId = columnId };
                result.Values.Add(item);

                if (!columns.TryGetValue(columnId, out var column))
                {
                    item.Error = "Parameter metadata is missing. Refresh the recipe.";
                    continue;
                }

                if (!Enum.IsDefined(column.Kind))
                {
                    item.Error = "Unknown recipe parameter type.";
                    continue;
                }

                if (stage.Number <= 0 || string.IsNullOrWhiteSpace(stage.TagPrefix) || string.IsNullOrWhiteSpace(column.TagSuffix))
                {
                    item.Error = "PLC tag prefix, stage number or parameter suffix is not assigned.";
                    continue;
                }

                // Суффикс используем буквально, включая AUTO_CONT_SET.
                item.TagName = $"{stage.TagPrefix.Trim()}_{stage.Number.ToString(CultureInfo.InvariantCulture)}_{column.TagSuffix.Trim()}";
            }
        }

        foreach (var group in result.Values.Where(item => item.Error is null).GroupBy(item => item.TagName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
                item.Error = "Duplicate PLC tag address. Check stage prefixes and parameter suffixes.";
        }

        var pending = result.Values.Where(item => item.Error is null).ToList();
        var stageTag = page.CurrentStageTag?.Trim() ?? "";
        var readStage = stageTag.Length > 0;

        if (!readStage)
            result.CurrentStageError = "CurrentStage tag is not configured.";

        // Повторяем только неудачные теги, один раз; CurrentStage входит в тот же пакет.
        for (var attempt = 0; attempt < 2 && (pending.Count > 0 || readStage); attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(350), ct);

            var tags = pending.Select(item => item.TagName).Concat(readStage ? new[] { stageTag } : Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var batch = await gateway.ReadTagsAsync(tags, ct);
            ct.ThrowIfCancellationRequested();

            var readings = batch.Items.ToDictionary(item => item.TagName, StringComparer.OrdinalIgnoreCase);

            foreach (var item in pending)
            {
                item.Error = ReadValueError(readings, item.TagName, columns[item.ColumnId].Kind, out var value);
                item.Value = item.Error is null ? value : null;
            }

            if (readStage)
            {
                var error = ReadValueError(readings, stageTag, RecipeParameterKind.InputField, out var value);

                if (error is null && (value < 0 || value > int.MaxValue || value != Math.Truncate(value)))
                    error = "CurrentStage must be a non-negative integer.";

                result.CurrentStage = error is null ? (int)value : null;
                result.CurrentStageError = error is null ? null : $"{stageTag}: {error}";
                readStage = error is not null;
            }

            pending = pending.Where(item => item.Error is not null).ToList();
        }

        result.ReadAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>Читает mode только по запросу назначения: 0 — ручной режим, 1 — автоматический.</summary>
    public static async Task<string?> ReadManualModeErrorAsync(RecipePageDto page, IPlantScadaGateway gateway, CancellationToken ct = default)
    {
        var tag = page.ModeTag?.Trim() ?? "";

        if (tag.Length == 0)
            return $"Mode tag is not configured for Unit {page.Page}.";

        var batch = await gateway.ReadTagsAsync([tag], ct);
        ct.ThrowIfCancellationRequested();

        var readings = batch.Items.ToDictionary(item => item.TagName, StringComparer.OrdinalIgnoreCase);
        var error = ReadValueError(readings, tag, RecipeParameterKind.CheckBox, out var mode);

        if (error is not null)
            return $"Cannot verify manual mode. {tag}: {error}";

        return mode == 0 ? null : $"Unit {page.Page} is in automatic mode. Switch it to manual mode before setting the Current recipe.";
    }

    /// <summary>Недоступность и плохое качество не преобразуются в ноль.</summary>
    private static string? ReadValueError(IReadOnlyDictionary<string, ScadaTagBatchReadItem> readings, string tag, RecipeParameterKind kind, out double value)
    {
        value = 0;

        if (!readings.TryGetValue(tag, out var item))
            return "PLC did not return this tag.";

        if (!item.Success || item.Quality is ScadaTagQuality.Bad or ScadaTagQuality.Uncertain)
            return string.IsNullOrWhiteSpace(item.Error) ? "PLC value is unavailable or has invalid quality." : item.Error;

        return TryReadValue(item.Value, kind, out value) ? null : "PLC returned an invalid numeric or Boolean value.";
    }

    /// <summary>Не допускает NaN/Infinity и произвольные числа вместо Boolean.</summary>
    public static bool TryReadValue(string? text, RecipeParameterKind kind, out double value)
    {
        text = text?.Trim();

        if (kind == RecipeParameterKind.CheckBox)
        {
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
            {
                value = 1;
                return true;
            }

            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
            {
                value = 0;
                return true;
            }
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && (kind != RecipeParameterKind.CheckBox || value is 0 or 1);
    }
}