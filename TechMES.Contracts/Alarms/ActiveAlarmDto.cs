namespace TechMES.Contracts.Alarms;

/// <summary>
/// Одна авария из текущей сводки Plant SCADA.
/// RawFields сохраняет значения CtApi для проверки соответствия полей конкретному проекту.
/// </summary>
public sealed class ActiveAlarmDto
{
    public string Tag { get; init; } = "";
    public string Description { get; init; } = "";
    public string Area { get; init; } = "";
    public string Category { get; init; } = "";
    public string State { get; init; } = "";
    public string OnDate { get; init; } = "";
    public string AckDate { get; init; } = "";
    public IReadOnlyDictionary<string, string> RawFields { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Результат одного опроса. Truncated показывает, что CtApi вернул больше строк,
/// чем мы разрешили читать за один цикл.
/// </summary>
public sealed class ActiveAlarmsResponse
{
    public DateTimeOffset LoadedAtUtc { get; init; }
    public IReadOnlyList<ActiveAlarmDto> Items { get; init; } = [];
    public bool Truncated { get; init; }
}