namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Итог расчета настроек регулятора для выбранной модели и метода.
/// </summary>
public sealed class PidTuneCalculationResult
{
    public double? Kp { get; init; }

    public double? Ti { get; init; }

    public double? Td { get; init; }

    public bool IsValid { get; init; }

    public string Message { get; init; } = "";

    public static PidTuneCalculationResult Valid(double kp, double ti, double td, string message)
    {
        return new PidTuneCalculationResult
        {
            Kp = kp,
            Ti = ti,
            Td = td,
            IsValid = true,
            Message = message
        };
    }

    public static PidTuneCalculationResult Invalid(string message)
    {
        return new PidTuneCalculationResult
        {
            IsValid = false,
            Message = message
        };
    }
}
