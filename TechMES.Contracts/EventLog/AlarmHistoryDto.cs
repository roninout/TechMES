namespace TechMES.Contracts.EventLog;

/// <summary>
/// Одна строка истории тревог из таблицы public.alarm_history.
/// </summary>
public sealed class AlarmHistoryDto
{
    /// <summary>
    /// Дата и время события тревоги.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Готовая строка времени для отображения.
    /// </summary>
    public string Time { get; set; } = "";

    /// <summary>
    /// Категория тревоги.
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// Пользователь, если он присутствует в источнике.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Location/area из alarm history.
    /// </summary>
    public string Location { get; set; } = "";

    /// <summary>
    /// Оборудование, к которому относится тревога.
    /// </summary>
    public string Equipment { get; set; } = "";

    /// <summary>
    /// Item/tag внутри оборудования.
    /// </summary>
    public string Item { get; set; } = "";

    /// <summary>
    /// Комментарий/описание тревоги.
    /// </summary>
    public string Comment { get; set; } = "";

    /// <summary>
    /// Состояние тревоги.
    /// </summary>
    public string State { get; set; } = "";
}
