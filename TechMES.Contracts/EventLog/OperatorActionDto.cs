namespace TechMES.Contracts.EventLog;

/// <summary>
/// Действие оператора из таблицы public."OperatorAct".
/// Эти строки в БД пишет SCADA/Cicode audit, а WEB только читает и отображает.
/// </summary>
public sealed class OperatorActionDto
{
    /// <summary>
    /// Дата и время действия.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Готовая строка времени для таблицы.
    /// </summary>
    public string Time { get; set; } = "";

    /// <summary>
    /// Тип действия из EventPicker/SCADA.
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Клиентское устройство, с которого выполнено действие.
    /// </summary>
    public string Client { get; set; } = "";

    /// <summary>
    /// Пользователь/оператор, если он был сохранен SCADA-логикой.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Tag, который менялся.
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// Имя оборудования.
    /// </summary>
    public string Equip { get; set; } = "";

    /// <summary>
    /// Описание действия. Может содержать страницу в квадратных скобках.
    /// </summary>
    public string Desc { get; set; } = "";

    /// <summary>
    /// Предыдущее значение.
    /// </summary>
    public string OldValue { get; set; } = "";

    /// <summary>
    /// Новое значение.
    /// </summary>
    public string NewValue { get; set; } = "";

    /// <summary>
    /// Страница/контекст действия, извлеченная из Desc вида "[Param] comment".
    /// </summary>
    public string Page => ExtractPage(Desc);

    /// <summary>
    /// Комментарий без префикса страницы.
    /// </summary>
    public string Comment => ExtractComment(Desc);

    /// <summary>
    /// Достает текст внутри первых квадратных скобок.
    /// </summary>
    private static string ExtractPage(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return "";

        var start = desc.IndexOf('[');
        var end = desc.IndexOf(']');

        return start >= 0 && end > start
            ? desc.Substring(start + 1, end - start - 1)
            : "";
    }

    /// <summary>
    /// Убирает из Desc префикс страницы и возвращает чистый комментарий.
    /// </summary>
    private static string ExtractComment(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return "";

        var start = desc.IndexOf('[');
        var end = desc.IndexOf(']');

        return start >= 0 && end > start
            ? desc.Remove(start, end - start + 1).Trim()
            : desc.Trim();
    }
}
