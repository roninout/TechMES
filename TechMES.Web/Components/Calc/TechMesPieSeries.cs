using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;

namespace TechMES.Web.Components.Calc;

/// <summary>
/// Расширение стандартного RadzenPieSeries.
/// Геометрия Pie, размер секторов и положение DataLabels полностью рассчитываются штатным Radzen.
/// ValueProperty определяет размер сектора, а DataLabelText позволяет независимо задать отображаемый внутри сектора текст.
/// Благодаря этому один и тот же компонент используется для Density, Capacity и будущих mixture calculations.
/// </summary>
public sealed class TechMesPieSeries<TItem> : RadzenPieSeries<TItem>
{
    /// <summary>
    /// Необязательная функция, возвращающая текст DataLabel для конкретного элемента Pie.
    /// Если функция не задана, сохраняется стандартное поведение Radzen и отображается ValueProperty.
    /// </summary>
    [Parameter]
    public Func<TItem, string>? DataLabelText { get; set; }

    /// <summary>
    /// Получаем уже рассчитанные Radzen DataLabels и изменяем только отображаемый текст.
    /// Положение, угол и координаты остаются полностью под управлением Radzen.
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