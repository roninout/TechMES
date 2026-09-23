using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

public partial class EquipmentParamPanel
{
    // Старый Graph остаётся в проекте до проверки новой реализации на реальном CtApi.
    private static readonly bool UseLegacyParamTrend = false;

    private IReadOnlyList<ScadaTrendSeries> _paramTrendSeries = [];

    /// <summary>
    /// Собирает серии Graph из Equipment items, которые Runtime вернул в snapshot.
    /// Общий график сам проверяет тренд и читает историю через /api/param/tags/trend.
    /// </summary>
    private void UpdateParamTrendSeries(ParamSnapshotResponse snapshot)
    {
        if (!snapshot.Supported || snapshot.TrendItems.Count == 0)
        {
            _paramTrendSeries = [];
            return;
        }

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
}