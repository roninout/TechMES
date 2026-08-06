namespace TechMES.Calc.Tanks.Models;

/// <summary>
/// Геометрические размеры прямоугольного резервуара.
///
/// Все размеры хранятся в миллиметрах, чтобы модель напрямую
/// соответствовала старой конфигурации TechParamsCalc.
/// </summary>
public sealed record RectangularTankGeometry(
    double HeightMm,
    double WidthMm,
    double LengthMm);

/// <summary>
/// Геометрические параметры подключения датчика уровня.
///
/// Названия сопоставлены со старой моделью:
/// DistanceToPointAMm = DistToDistanceA / ltoDistanceA;
/// DistanceAMm        = DistanceA;
/// DistanceBMm        = DistanceB.
/// </summary>
public sealed record TankLevelMeasurementGeometry(
    double DistanceToPointAMm,
    double DistanceAMm,
    double DistanceBMm);

/// <summary>
/// Входные данные одного расчёта объёма прямоугольного резервуара.
/// </summary>
public sealed record RectangularTankVolumeInput(
    double MeasuredLevelMm,
    RectangularTankGeometry Tank,
    TankLevelMeasurementGeometry Measurement);

/// <summary>
/// Типизированный результат расчёта.
///
/// Помимо конечного объёма содержит промежуточные величины,
/// необходимые для диагностики и будущего WEB-тестера.
/// </summary>
public sealed record RectangularTankVolumeCalculation(
    double CrossSectionAreaM2,
    double MeasurementSpanMm,
    double UnmeasuredBottomHeightMm,
    double NormalizedMeasuredLevelMm,
    double EffectiveLiquidHeightMm,
    double VolumeM3);