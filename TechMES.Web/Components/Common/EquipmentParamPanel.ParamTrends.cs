using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

public partial class EquipmentParamPanel
{
    // Старый Graph остаётся в проекте до проверки новой реализации на реальном CtApi.
    private static readonly bool UseLegacyParamTrend = false;

    private IReadOnlyList<ScadaTrendSeries> _paramTrendSeries = [];
    private double _paramTrendMinimum, _paramTrendMaximum = 1;

    /// <summary>
    /// Собирает серии Graph из Equipment items, которые Runtime вернул в snapshot.
    /// Общий график сам проверяет тренд и читает историю через /api/param/tags/trend.
    /// </summary>
    private void UpdateParamTrendSeries(ParamSnapshotResponse snapshot)
    {
        if (!snapshot.Supported || snapshot.TrendItems.Count == 0)
        {
            _paramTrendSeries = [];
            _paramTrendMinimum = 0;
            _paramTrendMaximum = 1;
            return;
        }

        (_paramTrendMinimum, _paramTrendMaximum) = ResolveParamTrendScale(snapshot);

        var resolvedItems = snapshot.Items.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var series = new List<ScadaTrendSeries>();

        foreach (var item in snapshot.TrendItems)
        {
            // TagName разрешён CtApi через TagInfo(Equipment.ITEM, 0).
            // Если online-чтение не удалось, оставляем Equipment.ITEM для поиска тренда в Runtime.
            resolvedItems.TryGetValue(item.Name, out var resolved);

            var tag = string.IsNullOrWhiteSpace(resolved?.TagName)
                ? $"{snapshot.EquipmentName}.{item.Name}"
                : resolved.TagName.Trim();

            if (!names.Add(tag))
                continue;

            series.Add(new ScadaTrendSeries
            {
                TagName = tag,
                Title = item.Name,
                Color = item.Color,
                Style = ScadaTrendStyle.Area,
                Unit = item.Name.Equals("R", StringComparison.OrdinalIgnoreCase) ? snapshot.Unit ?? "" : ""
            });
        }

        _paramTrendSeries = series;
    }

    /// <summary>
    /// Задаёт общую фиксированную шкалу для всех серий Param.
    /// Для AI/VGA берёт MinR/MaxR; собственные границы других серий объединяет.
    /// </summary>
    private static (double Minimum, double Maximum) ResolveParamTrendScale(ParamSnapshotResponse snapshot)
    {
        var minR = snapshot.Items.FirstOrDefault(item => item.Name.Equals("MinR", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var maxR = snapshot.Items.FirstOrDefault(item => item.Name.Equals("MaxR", StringComparison.OrdinalIgnoreCase))?.NumericValue;
        var hasEquipmentRange = minR.HasValue && maxR.HasValue && double.IsFinite(minR.Value) && double.IsFinite(maxR.Value);
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;

        foreach (var item in snapshot.TrendItems)
        {
            // Сначала берём собственные границы серии. Если их нет — MinR/MaxR.
            // Запасной диапазон 0..1 соответствует существующей логике CtApi provider.
            var hasNativeRange = item.NativeMin.HasValue && item.NativeMax.HasValue && double.IsFinite(item.NativeMin.Value) && double.IsFinite(item.NativeMax.Value);
            var lower = hasNativeRange ? item.NativeMin!.Value : hasEquipmentRange ? minR!.Value : 0d;
            var upper = hasNativeRange ? item.NativeMax!.Value : hasEquipmentRange ? maxR!.Value : 1d;

            minimum = Math.Min(minimum, Math.Min(lower, upper));
            maximum = Math.Max(maximum, Math.Max(lower, upper));
        }

        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return (0, 1);

        return maximum > minimum ? (minimum, maximum) : (minimum, minimum + 1);
    }
}