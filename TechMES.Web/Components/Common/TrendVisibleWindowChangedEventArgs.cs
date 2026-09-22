namespace TechMES.Web.Components.Common;

/// <summary>
/// Реальный видимый диапазон общего компонента тренда.
/// Используется для расчётов по выбранному пользователем участку.
/// </summary>
public sealed class TrendVisibleWindowChangedEventArgs : EventArgs
{
    /// <summary>Создаёт уведомление о диапазоне и режиме графика.</summary>
    public TrendVisibleWindowChangedEventArgs(DateTime fromUtc, DateTime toUtc, string viewKey, bool isLive = false)
    {
        FromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        ToUtc = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
        ViewKey = viewKey;
        IsLive = isLive;
    }

    /// <summary>Левая граница видимого окна в UTC.</summary>
    public DateTime FromUtc { get; }

    /// <summary>Правая граница видимого окна в UTC.</summary>
    public DateTime ToUtc { get; }

    /// <summary>Ключ версии графика для отбрасывания устаревших событий.</summary>
    public string ViewKey { get; }

    /// <summary>True, когда график следует за текущим временем.</summary>
    public bool IsLive { get; }
}