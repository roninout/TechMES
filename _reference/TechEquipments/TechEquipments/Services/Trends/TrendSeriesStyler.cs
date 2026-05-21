using DevExpress.Xpf.Charts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace TechEquipments
{
    /// <summary>
    /// Применяет [TrendSeriesStyle] к auto-series DevExpress ChartControl.
    /// 
    /// ВАЖНО:
    /// - CurrentParamModel теперь может быть оберткой AiModel/DiModel/...
    /// - атрибуты [TrendSeriesStyle] лежат на внутреннем AIParam/DIParam/...
    /// - поэтому перед чтением атрибутов всегда делаем Unwrap(...)
    /// </summary>
    public static class TrendSeriesStyler
    {
        public static void Apply(ChartControl chart, object? model)
        {
            if (chart?.Diagram is not XYDiagram2D d)
                return;

            var styleMap = GetSeriesStyleMap(model);

            foreach (var s in d.Series)
            {
                var key = GetSeriesKey(s);
                if (key.Length == 0)
                    continue;

                // Чтобы легенда/tooltip были стабильно "R", "Mode", "AlarmOpen" и т.д.
                s.DisplayName = key;

                if (!styleMap.TryGetValue(key, out var st))
                    continue;

                // 1) Заливка
                s.GetType().GetProperty("Brush")?.SetValue(s, st.brush);

                // 2) Transparency
                var pTr = s.GetType().GetProperty("Transparency");
                if (pTr != null && pTr.CanWrite)
                {
                    if (pTr.PropertyType == typeof(double))
                        pTr.SetValue(s, st.transparency);
                    else if (pTr.PropertyType == typeof(float))
                        pTr.SetValue(s, (float)st.transparency);
                }

                // 3) Линия
                var propLineStyle = s.GetType().GetProperty("LineStyle");
                if (propLineStyle != null)
                {
                    var ls = propLineStyle.GetValue(s) ?? Activator.CreateInstance(propLineStyle.PropertyType);
                    if (ls != null)
                    {
                        ls.GetType().GetProperty("Brush")?.SetValue(ls, st.brush);
                        propLineStyle.SetValue(s, ls);
                    }
                }
                else
                {
                    s.GetType().GetProperty("BorderBrush")?.SetValue(s, st.brush);
                }
            }
        }

        private static string GetSeriesKey(Series s)
        {
            var dmProp = s.GetType().GetProperty("DataMember");
            var dm = dmProp?.GetValue(s) as string;
            if (!string.IsNullOrWhiteSpace(dm))
                return dm.Trim();

            var dn = (s.DisplayName ?? "").Trim();
            if (dn.Length > 0)
                return dn;

            var n = (s.Name ?? "").Trim();
            if (n.Length > 0)
                return n;

            return "";
        }

        private static Dictionary<string, (Brush brush, double transparency)> GetSeriesStyleMap(object? model)
        {
            var map = new Dictionary<string, (Brush, double)>(StringComparer.OrdinalIgnoreCase);

            // ВАЖНО: читаем атрибуты не с обертки, а с внутреннего Param
            var rawModel = ParamModelHelper.Unwrap(model);
            if (rawModel == null)
                return map;

            var t = rawModel.GetType();
            var attrs = t.GetCustomAttributes(typeof(TrendSeriesStyleAttribute), inherit: true)
                         .OfType<TrendSeriesStyleAttribute>();

            foreach (var a in attrs)
            {
                if (string.IsNullOrWhiteSpace(a.Item) || string.IsNullOrWhiteSpace(a.Color))
                    continue;

                Color c;
                try
                {
                    c = (Color)ColorConverter.ConvertFromString(a.Color);
                }
                catch
                {
                    continue;
                }

                var brush = new SolidColorBrush(c);
                brush.Freeze();

                var tr = a.Transparency;
                if (tr < 0) tr = 0;
                if (tr > 1) tr = 1;

                map[a.Item.Trim()] = (brush, tr);
            }

            return map;
        }
    }
}
