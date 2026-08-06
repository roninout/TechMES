using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Запрос на ручное выполнение одного алгоритма.
///
/// Параметры передаются как JSON-значения, потому что разные алгоритмы
/// могут использовать числа, строки, флаги и Selection.
/// </summary>
public sealed class CalcTestRequest
{
    /// <summary>
    /// Стабильный код алгоритма, например tank.volume.rectangular.
    /// </summary>
    public string DefinitionCode { get; set; } = "";

    /// <summary>
    /// Значения входных параметров по их стабильным ключам.
    ///
    /// Runtime преобразует JsonElement в обычные CLR-значения до передачи
    /// в TechMES.Calc. Поэтому расчётное ядро не зависит от System.Text.Json.
    /// </summary>
    public Dictionary<string, JsonElement>? Parameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Запрашивать ли подробные промежуточные значения.
    /// </summary>
    public bool IncludeTrace { get; set; } = true;
}

/// <summary>
/// Уровень сообщения, возвращённого алгоритмом.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcMessageSeverityDto
{
    Information,
    Warning
}

/// <summary>
/// Одно фактически рассчитанное выходное значение.
/// </summary>
public sealed class CalcOutputDto
{
    /// <summary>
    /// Стабильный ключ результата.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Отображаемое название.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Рассчитанное значение.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Единица измерения.
    /// </summary>
    public string? Unit { get; set; }
}

/// <summary>
/// Информационное сообщение или предупреждение алгоритма.
/// </summary>
public sealed class CalcMessageDto
{
    /// <summary>
    /// Стабильный код сообщения.
    /// </summary>
    public string Code { get; set; } = "";

    /// <summary>
    /// Текст сообщения.
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Уровень сообщения.
    /// </summary>
    public CalcMessageSeverityDto Severity { get; set; }
}

/// <summary>
/// Одно промежуточное диагностическое значение.
/// </summary>
public sealed class CalcTraceItemDto
{
    /// <summary>
    /// Стабильный ключ диагностического значения.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Отображаемое название.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Отформатированное значение.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Единица измерения.
    /// </summary>
    public string? Unit { get; set; }
}

/// <summary>
/// Результат ручного выполнения одного алгоритма.
/// </summary>
public sealed class CalcTestResponse
{
    /// <summary>
    /// Код выполненного алгоритма.
    /// </summary>
    public string DefinitionCode { get; set; } = "";

    /// <summary>
    /// Версия выполненного алгоритма.
    /// </summary>
    public string DefinitionVersion { get; set; } = "";

    /// <summary>
    /// Признак успешного выполнения.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Рассчитанные значения.
    /// </summary>
    public List<CalcOutputDto> Outputs { get; set; } = [];

    /// <summary>
    /// Информационные сообщения и предупреждения.
    /// </summary>
    public List<CalcMessageDto> Messages { get; set; } = [];

    /// <summary>
    /// Промежуточные диагностические значения.
    /// </summary>
    public List<CalcTraceItemDto> Trace { get; set; } = [];

    /// <summary>
    /// Стабильный код ошибки.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Текст ошибки.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Простой ответ для ошибок уровня HTTP API.
///
/// Используется, когда алгоритм не найден либо запрос не содержит
/// обязательного DefinitionCode.
/// </summary>
public sealed class CalcApiErrorResponse
{
    /// <summary>
    /// Стабильный код ошибки.
    /// </summary>
    public string ErrorCode { get; set; } = "";

    /// <summary>
    /// Текст ошибки.
    /// </summary>
    public string ErrorMessage { get; set; } = "";
}