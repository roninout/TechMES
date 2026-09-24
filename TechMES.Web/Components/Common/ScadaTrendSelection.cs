namespace TechMES.Web.Components.Common;

/// <summary>
/// Исходные точки загруженной истории за текущее окно графика.
/// Значения не преобразованы к общей шкале и не прорежены для отображения.
/// </summary>
public sealed record ScadaTrendSelection(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<ScadaTrendSample> Samples);

public sealed record ScadaTrendSample(string Series, DateTime TimeUtc, double RawValue);