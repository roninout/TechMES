namespace TechMES.Calc.Constants;

/// <summary>
/// Физические константы Calculation Engine.
///
/// Пока значение хранится непосредственно в коде.
/// Позже источник этого параметра будет вынесен в TechMES.Maintenance,
/// но все расчёты уже сейчас обращаются к одной общей точке.
/// </summary>
public static class CalculationPhysicalConstants
{
    /// <summary>
    /// Атмосферное давление, используемое для перевода
    /// измеренного избыточного Pressure в абсолютное:
    ///
    /// P(abs) = P(g) + AtmosphericPressureBarAbsolute.
    /// </summary>
    public const double AtmosphericPressureBarAbsolute = 1.0d; // 1.01325d
}
