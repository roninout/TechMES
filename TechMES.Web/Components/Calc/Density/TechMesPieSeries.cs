using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;

namespace TechMES.Web.Components.Calc.Density;

/// <summary>
/// Расширение стандартного RadzenPieSeries.
///
/// Геометрия Pie, размер секторов и положение DataLabels
/// полностью рассчитываются штатным Radzen.
///
/// Нам требуется только разделить два понятия:
///
/// - Percent определяет размер сектора Pie;
/// - DensityKgPerM3 отображается текстом внутри этого сектора.
///
/// Поэтому никаких собственных Math.Sin/Math.Cos,
/// абсолютных HTML-слоёв и ручного расчёта координат здесь нет.
/// </summary>
public sealed class TechMesPieSeries<TItem> : RadzenPieSeries<TItem>
{
    /// <summary>
    /// Необязательная функция, которая возвращает текст,
    /// отображаемый внутри соответствующего сектора Pie.
    ///
    /// Если функция не задана, компонент полностью сохраняет
    /// стандартное поведение Radzen и отображает ValueProperty.
    /// </summary>
    [Parameter]
    public Func<TItem, string>? DataLabelText { get; set; }

    /// <summary>
    /// Получаем готовые DataLabels от Radzen.
    ///
    /// Важно: сам Radzen уже определил:
    /// - сектор;
    /// - угол;
    /// - положение подписи;
    /// - Inside/Center/Auto;
    /// - координаты.
    ///
    /// Мы изменяем только Text и совершенно не вмешиваемся
    /// в геометрию Pie.
    /// </summary>
    public override IEnumerable<ChartDataLabel> GetDataLabels(double offsetX, double offsetY, DataLabelPosition position)
    {
        var labels = base.GetDataLabels(offsetX, offsetY, position).ToList();

        if (DataLabelText is null)
            return labels;

        var items = PositiveItems;
        var count = Math.Min(labels.Count, items.Count);

        for (var index = 0; index < count; index++)
            labels[index].Text = DataLabelText(items[index]);

        return labels;
    }
}