namespace TechMES.Calc.Results;

/// <summary>
/// Уровень информационного сообщения расчёта.
/// Ошибки представлены отдельно через ErrorCode и ErrorMessage.
/// </summary>
public enum CalculationMessageSeverity
{
    Information,
    Warning
}

/// <summary>
/// Описывает один доступный выход алгоритма.
/// </summary>
public sealed record CalculationOutputDefinition(
    string Key,
    string Name,
    string? Unit = null,
    int Decimals = 2,
    int Order = 0,
    string? Description = null);

/// <summary>
/// Содержит одно фактически рассчитанное значение.
/// </summary>
public sealed record CalculationOutput(
    string Key,
    string Name,
    double Value,
    string? Unit = null);

/// <summary>
/// Содержит информационное сообщение или предупреждение.
/// </summary>
public sealed record CalculationMessage(
    string Code,
    string Message,
    CalculationMessageSeverity Severity);

/// <summary>
/// Содержит одно промежуточное значение расчёта.
///
/// Trace предназначен для диагностики и WEB-тестера.
/// В рабочем цикле его можно отключать для уменьшения нагрузки.
/// </summary>
public sealed record CalculationTraceItem(
    string Key,
    string Name,
    string Value,
    string? Unit = null);

/// <summary>
/// Универсальный результат выполнения любого алгоритма.
/// </summary>
public sealed class CalculationResult
{
    /// <summary>
    /// Показывает, завершился ли расчёт успешно.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Рассчитанные выходные значения.
    /// </summary>
    public IReadOnlyList<CalculationOutput> Outputs { get; init; } =
        Array.Empty<CalculationOutput>();

    /// <summary>
    /// Информационные сообщения и предупреждения.
    /// </summary>
    public IReadOnlyList<CalculationMessage> Messages { get; init; } =
        Array.Empty<CalculationMessage>();

    /// <summary>
    /// Промежуточные диагностические значения.
    /// </summary>
    public IReadOnlyList<CalculationTraceItem> Trace { get; init; } =
        Array.Empty<CalculationTraceItem>();

    /// <summary>
    /// Программный код ошибки для неуспешного результата.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Текст ошибки для журнала и пользователя.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Создаёт успешный результат расчёта.
    /// </summary>
    public static CalculationResult Success(
        IEnumerable<CalculationOutput> outputs,
        IEnumerable<CalculationMessage>? messages = null,
        IEnumerable<CalculationTraceItem>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        return new CalculationResult
        {
            IsSuccess = true,
            Outputs = outputs.ToArray(),
            Messages = messages?.ToArray() ?? Array.Empty<CalculationMessage>(),
            Trace = trace?.ToArray() ?? Array.Empty<CalculationTraceItem>()
        };
    }

    /// <summary>
    /// Создаёт неуспешный результат расчёта.
    /// </summary>
    public static CalculationResult Failure(
        string errorCode,
        string errorMessage,
        IEnumerable<CalculationMessage>? messages = null,
        IEnumerable<CalculationTraceItem>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Calculation error code is required.", nameof(errorCode));

        if (string.IsNullOrWhiteSpace(errorMessage))
            throw new ArgumentException("Calculation error message is required.", nameof(errorMessage));

        return new CalculationResult
        {
            IsSuccess = false,
            ErrorCode = errorCode.Trim(),
            ErrorMessage = errorMessage.Trim(),
            Messages = messages?.ToArray() ?? Array.Empty<CalculationMessage>(),
            Trace = trace?.ToArray() ?? Array.Empty<CalculationTraceItem>()
        };
    }
}