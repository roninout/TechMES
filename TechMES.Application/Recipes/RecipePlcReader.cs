using System.Globalization;
using TechMES.Application.Scada;
using TechMES.Contracts.Recipes;
using TechMES.Contracts.Scada;

namespace TechMES.Application.Recipes;

/// <summary>Читает параметры сохранённого рецепта через общий SCADA gateway.</summary>
public static class RecipePlcReader
{
    public static async Task<RecipePlcSnapshotDto> ReadAsync(RecipeSnapshotDto snapshot, int pageId, IPlantScadaGateway gateway, CancellationToken ct = default)
    {
        var result = new RecipePlcSnapshotDto { RecipeId = snapshot.Recipe.Id, PageId = pageId, LayoutStamp = RecipePlcLayout.GetStamp(snapshot) };
        var columns = snapshot.Columns.ToDictionary(column => column.Id);
        var kinds = new Dictionary<(int StageId, int ColumnId), RecipeParameterKind>();

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

                kinds[(stage.Id, columnId)] = column.Kind;

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

                // Используем суффикс из БД буквально, включая AUTO_CONT_SET.
                var suffix = column.TagSuffix.Trim();

                item.TagName = $"{stage.TagPrefix.Trim()}_{stage.Number.ToString(CultureInfo.InvariantCulture)}_{suffix}";
            }
        }

        // Две разные ячейки не должны случайно читать один адрес PLC.
        foreach (var group in result.Values.Where(item => item.Error is null).GroupBy(item => item.TagName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
                item.Error = "Duplicate PLC tag address. Check stage prefixes and parameter suffixes.";
        }

        var pending = result.Values.Where(item => item.Error is null).ToList();

        // Повторяем только неудачные чтения, один раз. Ошибки структуры сюда не входят.
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(350), ct);

            // Один логический batch. Сам gateway последовательно работает с общим CtApi.
            var batch = await gateway.ReadTagsAsync(pending.Select(item => item.TagName).ToArray(), ct);
            ct.ThrowIfCancellationRequested();

            var readings = batch.Items.ToDictionary(item => item.TagName, StringComparer.OrdinalIgnoreCase);

            foreach (var item in pending)
            {
                item.Error = null;
                item.Value = null;

                if (!readings.TryGetValue(item.TagName, out var reading))
                {
                    item.Error = "PLC did not return this tag.";
                }
                else if (!reading.Success || reading.Quality is ScadaTagQuality.Bad or ScadaTagQuality.Uncertain)
                {
                    item.Error = reading.Error ?? "PLC value is unavailable or has invalid quality.";
                }
                else if (TryReadValue(reading.Value, kinds[(item.StageId, item.ColumnId)], out var value))
                {
                    item.Value = value;
                }
                else
                {
                    item.Error = "PLC returned an invalid numeric or Boolean value.";
                }
            }

            pending = pending.Where(item => item.Error is not null).ToList();
        }

        result.ReadAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>Не допускает NaN/Infinity и не принимает произвольное число за Boolean.</summary>
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