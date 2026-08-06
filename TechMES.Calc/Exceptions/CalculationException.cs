namespace TechMES.Calc.Exceptions;

/// <summary>
/// Представляет ожидаемую ошибку расчётного ядра.
///
/// Такие ошибки возникают при неправильных входных параметрах,
/// отсутствии обязательных данных или недопустимой конфигурации.
///
/// Code используется программой для определения типа ошибки,
/// Message предназначен для журнала и отображения пользователю.
/// </summary>
public sealed class CalculationException : Exception
{
    public CalculationException(string code, string message) : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Calculation error code is required.", nameof(code));

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Calculation error message is required.", nameof(message));

        Code = code.Trim();
    }

    /// <summary>
    /// Стабильный программный код ошибки.
    /// </summary>
    public string Code { get; }
}