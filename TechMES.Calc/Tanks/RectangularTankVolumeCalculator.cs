using TechMES.Calc.Exceptions;
using TechMES.Calc.Tanks.Models;

namespace TechMES.Calc.Tanks;

/// <summary>
/// Выполняет типизированный расчёт объёма прямоугольного резервуара.
///
/// Математическое поведение версии 1 совместимо со старым Tank Type 4:
/// отрицательный измеренный уровень принимается равным нулю,
/// а превышение полной высоты резервуара пока не обрезается.
/// </summary>
public static class RectangularTankVolumeCalculator
{
    private const double MillimetersPerMeter = 1000.0;

    /// <summary>
    /// Рассчитывает объём и возвращает промежуточные величины.
    /// </summary>
    public static RectangularTankVolumeCalculation Calculate(RectangularTankVolumeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Tank);
        ArgumentNullException.ThrowIfNull(input.Measurement);

        ValidateInput(input);

        // Площадь горизонтального сечения прямоугольного резервуара.
        var crossSectionAreaM2 = input.Tank.WidthMm / MillimetersPerMeter * (input.Tank.LengthMm / MillimetersPerMeter);

        // Рабочий диапазон измерения датчика между точками A и B.
        var measurementSpanMm = input.Measurement.DistanceBMm - input.Measurement.DistanceAMm;

        // Часть резервуара ниже измеряемого диапазона датчика.
        var unmeasuredBottomHeightMm = Math.Max(0, input.Tank.HeightMm - (input.Measurement.DistanceToPointAMm + measurementSpanMm));

        // Для совместимости отрицательное показание принимается равным нулю.
        var normalizedMeasuredLevelMm = Math.Max(0, input.MeasuredLevelMm);

        // Полная расчётная высота жидкости с учётом нижнего остатка.
        var effectiveLiquidHeightMm = unmeasuredBottomHeightMm + normalizedMeasuredLevelMm;

        // Переводим высоту из миллиметров в метры и получаем объём в м³.
        var volumeM3 = crossSectionAreaM2 * effectiveLiquidHeightMm / MillimetersPerMeter;

        if (!double.IsFinite(volumeM3))
        {
            throw new CalculationException(
                "tank.volume.not-finite",
                "Calculated tank volume is not a finite number.");
        }

        return new RectangularTankVolumeCalculation(
            CrossSectionAreaM2: crossSectionAreaM2,
            MeasurementSpanMm: measurementSpanMm,
            UnmeasuredBottomHeightMm: unmeasuredBottomHeightMm,
            NormalizedMeasuredLevelMm: normalizedMeasuredLevelMm,
            EffectiveLiquidHeightMm: effectiveLiquidHeightMm,
            VolumeM3: volumeM3);
    }

    /// <summary>
    /// Проверяет геометрию резервуара и параметры датчика.
    ///
    /// Проверки не должны вводить ограничений, которых не было
    /// в старой рабочей формуле, если такие ограничения не следуют
    /// непосредственно из математического смысла параметров.
    /// </summary>
    private static void ValidateInput(RectangularTankVolumeInput input)
    {
        ValidateFinite(
            input.MeasuredLevelMm,
            "tank.level.not-finite",
            "Measured tank level must be a finite number.");

        ValidatePositive(
            input.Tank.HeightMm,
            "tank.geometry.height-invalid",
            "Tank height must be greater than zero.");

        ValidatePositive(
            input.Tank.WidthMm,
            "tank.geometry.width-invalid",
            "Tank width must be greater than zero.");

        ValidatePositive(
            input.Tank.LengthMm,
            "tank.geometry.length-invalid",
            "Tank length must be greater than zero.");

        ValidateNonNegative(
            input.Measurement.DistanceToPointAMm,
            "tank.measurement.distance-to-a-invalid",
            "Distance to point A cannot be negative.");

        ValidateNonNegative(
            input.Measurement.DistanceAMm,
            "tank.measurement.distance-a-invalid",
            "Distance A cannot be negative.");

        ValidateNonNegative(
            input.Measurement.DistanceBMm,
            "tank.measurement.distance-b-invalid",
            "Distance B cannot be negative.");

        /*
         * DistanceA и DistanceB задают границы одного измерительного диапазона,
         * поэтому их порядок можно проверять.
         *
         * DistanceToPointA описывает другое геометрическое расстояние.
         * Сравнивать его напрямую с DistanceA нельзя: старая формула
         * такого ограничения не содержала.
         */
        if (input.Measurement.DistanceBMm < input.Measurement.DistanceAMm)
        {
            throw new CalculationException(
                "tank.measurement.distance-order-invalid",
                "Distance B cannot be less than Distance A.");
        }
    }

    /// <summary>
    /// Проверяет положительное конечное значение.
    /// </summary>
    private static void ValidatePositive(double value, string errorCode, string errorMessage)
    {
        ValidateFinite(value, errorCode, errorMessage);

        if (value <= 0)
            throw new CalculationException(errorCode, errorMessage);
    }

    /// <summary>
    /// Проверяет неотрицательное конечное значение.
    /// </summary>
    private static void ValidateNonNegative(double value, string errorCode, string errorMessage)
    {
        ValidateFinite(value, errorCode, errorMessage);

        if (value < 0)
            throw new CalculationException(errorCode, errorMessage);
    }

    /// <summary>
    /// Проверяет, что значение не является NaN или Infinity.
    /// </summary>
    private static void ValidateFinite(double value, string errorCode, string errorMessage)
    {
        if (!double.IsFinite(value))
            throw new CalculationException(errorCode, errorMessage);
    }
}