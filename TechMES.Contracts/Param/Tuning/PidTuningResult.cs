namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Результат автоматической идентификации процесса и расчета PID-настроек.
/// </summary>
public sealed class PidTuningResult
{
    /// <summary>
    /// true, если расчет успешно нашел ступень OUT, реакцию PV и PID-настройки.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Текст причины, если расчет невозможен на выбранном участке тренда.
    /// </summary>
    public string ErrorMessage { get; init; } = "";

    /// <summary>
    /// Усиление процесса: delta PV / delta OUT.
    /// </summary>
    public double K { get; init; }

    /// <summary>
    /// Постоянная времени процесса, сек.
    /// </summary>
    public double T { get; init; }

    /// <summary>
    /// Запаздывание процесса, сек.
    /// </summary>
    public double Theta { get; init; }

    /// <summary>
    /// Пропорциональный коэффициент регулятора.
    /// </summary>
    public double Kp { get; init; }

    /// <summary>
    /// Время интегрирования, сек.
    /// </summary>
    public double Ti { get; init; }

    /// <summary>
    /// Время дифференцирования, сек.
    /// </summary>
    public double Td { get; init; }

    /// <summary>
    /// Оцененный шаг дискретизации выбранных данных, сек.
    /// </summary>
    public double DtSeconds { get; init; }

    /// <summary>
    /// Количество пар PV/OUT, реально использованных расчетом.
    /// </summary>
    public int PointsUsed { get; init; }

    /// <summary>
    /// Время найденной ступени OUT.
    /// </summary>
    public DateTime? StepTimeUtc { get; init; }
}
