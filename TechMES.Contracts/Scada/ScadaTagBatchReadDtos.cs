using System.Text.Json.Serialization;

namespace TechMES.Contracts.Scada;

/// <summary>
/// Качество прочитанного SCADA-значения.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScadaTagQuality
{
    /// <summary>
    /// Провайдер пока не предоставляет нативное качество тега.
    /// </summary>
    Unknown,

    /// <summary>
    /// Значение считается корректным.
    /// </summary>
    Good,

    /// <summary>
    /// Значение получено, но его качество вызывает сомнение.
    /// </summary>
    Uncertain,

    /// <summary>
    /// Значение не должно использоваться в расчёте.
    /// </summary>
    Bad
}

/// <summary>
/// Запрос пакетного чтения SCADA-тегов.
/// </summary>
public sealed class ScadaTagBatchReadRequest
{
    /// <summary>
    /// Имена тегов. Runtime удалит дубликаты без учёта регистра.
    /// </summary>
    public List<string> TagNames { get; set; } = [];

    // ============================================================
    // Реальная структура Equipment из Plant SCADA.
    // Key   = ITEM Equipment Type.
    // Value = реальный Variable Tag.
    // ============================================================
    public Dictionary<string, string> ItemTags { get; set; } = [];
}

/// <summary>
/// Результат чтения одного тега внутри batch.
/// </summary>
public sealed class ScadaTagBatchReadItem
{
    public string TagName { get; set; } = "";
    public string? Value { get; set; }

    /// <summary>
    /// Время завершения чтения Runtime в UTC.
    ///
    /// Это пока не нативная timestamp из Plant SCADA:
    /// текущий CtApi wrapper её не предоставляет.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; set; }

    public ScadaTagQuality Quality { get; set; } = ScadaTagQuality.Unknown;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Ответ пакетного чтения.
/// Ошибка одного тега не отменяет результаты остальных тегов.
/// </summary>
public sealed class ScadaTagBatchReadResponse
{
    public DateTimeOffset ReadAtUtc { get; set; }
    public int RequestedCount { get; set; }
    public int UniqueCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public List<ScadaTagBatchReadItem> Items { get; set; } = [];
}

/// <summary>
/// Ошибка структуры batch-запроса.
/// </summary>
public sealed class ScadaTagBatchReadErrorResponse
{
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}