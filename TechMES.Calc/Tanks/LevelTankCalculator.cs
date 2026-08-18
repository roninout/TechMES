namespace TechMES.Calc.Tanks;

/// <summary>
/// Общая логика LevelTank.
///
/// Геометрия Tank сюда не входит.
/// Калькулятор отвечает только за:
///
/// Level.R, %
/// -> рабочая зона измерения
/// -> измеренный Level, mm
/// -> физическая высота жидкости от дна
/// -> Volume
/// -> Mass.
/// </summary>
public static class LevelTankCalculator
{
    /// <summary>
    /// Выполняет полный расчёт LevelTank.
    ///
    /// totalLengthMm     - полный размер Tank по направлению измерения;
    /// lowerDeadAreaMm   - нижняя мёртвая зона;
    /// upperDeadAreaMm   - верхняя мёртвая зона;
    /// calculateAbove100 - разрешает продолжать расчёт Volume выше 100 %
    ///                     внутри upper dead area.
    ///
    /// Level при этом НИКОГДА не ограничивается сверху 100 %.
    /// Ограничивается только физическая высота, передаваемая
    /// в геометрический расчёт Volume.
    /// </summary>
    public static LevelTankResult Calculate(double levelRaw, double densityHmi, double totalLengthMm, double lowerDeadAreaMm, double upperDeadAreaMm, bool calculateAbove100, Func<double, double> calculateVolume)
    {
        ArgumentNullException.ThrowIfNull(calculateVolume);

        var measurementAreaMm = totalLengthMm - lowerDeadAreaMm - upperDeadAreaMm;

        /*
         * Level.R приходит в реальных процентах.
         *
         * 58.6 -> 58.6 %
         *
         * Снизу сохраняем старую защиту:
         * отрицательный Level не используем.
         *
         * Сверху НЕ ограничиваем:
         * 105 %, 110 % и т.д. остаются допустимыми.
         */
        var levelMm = Math.Max(0.0, measurementAreaMm * levelRaw / 100.0);

        /*
         * Физическая высота жидкости от самого нижнего края Tank.
         *
         * При 0 %:
         * liquidHeight = lowerDeadArea.
         *
         * При 100 %:
         * liquidHeight = lowerDeadArea + measurementArea.
         */
        var liquidHeightMm = lowerDeadAreaMm + levelMm;

        /*
         * Если расчёт выше 100 % запрещён,
         * Volume останавливается на верхней границе рабочей зоны.
         *
         * Сам Level при этом продолжает отображаться > 100 %.
         */
        if (!calculateAbove100)
            liquidHeightMm = Math.Min(liquidHeightMm, lowerDeadAreaMm + measurementAreaMm);

        /*
         * Даже при calculateAbove100=true нельзя считать
         * виртуальный объём выше физической геометрии Tank.
         */
        liquidHeightMm = Math.Clamp(liquidHeightMm, 0.0, totalLengthMm);

        var volumeM3 = calculateVolume(liquidHeightMm);
        var massT = densityHmi > 0 ? volumeM3 * densityHmi * 0.001 : 0.0;

        return new LevelTankResult(measurementAreaMm, levelMm, liquidHeightMm, volumeM3, massT);
    }
}


/// <summary>
/// Результат одного расчёта LevelTank.
///
/// HMaxMm         -> *_H_MAX
/// LevelMm        -> *_H_HMI
/// VolumeM3       -> *_V_HMI
/// MassT          -> *_M_HMI
///
/// LiquidHeightMm является внутренней физической координатой
/// и в Tank structure не записывается.
/// </summary>
public sealed record LevelTankResult(double HMaxMm, double LevelMm, double LiquidHeightMm, double VolumeM3, double MassT);