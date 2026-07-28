namespace TechMES.Web.Components.Common;

/// <summary>
/// Диапазон времени, который сейчас реально виден в ECharts.
/// Нужен для расчетов, где пользователь сам выбирает рабочий участок тренда.
/// </summary>
public sealed class TrendVisibleWindowChangedEventArgs : EventArgs
{
    public TrendVisibleWindowChangedEventArgs(DateTime fromUtc, DateTime toUtc, string viewKey)
    {
        FromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        ToUtc = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
        ViewKey = viewKey;
    }

    /// <summary>
    /// Левая граница видимого окна в UTC.
    /// </summary>
    public DateTime FromUtc { get; }

    /// <summary>
    /// Правая граница видимого окна в UTC.
    /// </summary>
    public DateTime ToUtc { get; }

    /// <summary>
    /// Ключ версии графика, из которой пришло событие.
    /// Родитель отбрасывает запоздавшие callback-и предыдущего render-а.
    /// </summary>
    public string ViewKey { get; }
}
