namespace TechMES.Calc.Tanks;

/// <summary>
/// Общая legacy-compatible логика LevelTank.
///
/// Старый TechParamsCalc выполнял расчёт в два этапа:
///
/// 1. По сырым показаниям Level.Val_R вычислялся физический уровень, mm.
/// 2. Tank по этому уровню вычислял Volume.
/// 3. По Density.ValHmi вычислялась Mass.
///
/// Геометрия конкретного Tank Type сюда намеренно не перенесена.
/// Она остаётся в TankType1...TankType8.
/// </summary>
public static class LevelTankCalculator
{
    /// <summary>
    /// Выполняет полный расчёт LevelTank.
    ///
    /// Формулы намеренно повторяют старый рабочий TechParamsCalc.
    /// </summary>
    public static LevelTankResult Calculate(double levelRaw, double densityHmi, double distanceA, double distanceB, Func<int, double> calculateVolume)
    {
        ArgumentNullException.ThrowIfNull(calculateVolume);

         // Старый LevelTank:
         // LevelMm = Math.Max(0, (int)((DistanceB - DistanceA) * Level.Val_R * 10 / 10000));
         //
         // Важно:
         // - сохраняем коэффициенты 10 / 10000;
         // - сохраняем именно приведение к int, без Round();
         // - Math.Max выполняется после приведения.
        var levelMm = Math.Max(0, (int)((distanceB - distanceA) * levelRaw * 10.0 / 1000.0));

        
        //* H_MAX в старом LevelTankCreatorCtApi: DistanceB - DistanceA
        var hMaxMm = (int)(distanceB - distanceA);

        // Геометрическая формула остаётся внутри конкретного Tank Type.
        var volumeM3 = calculateVolume(levelMm);

         // Старый LevelTank.Mass:
         //
         // if (Density != null && Density.ValHmi > 0)
         //     return Volume * Density.ValHmi * 0.0001;
         //
         // Density теперь передаётся значением, поэтому проверяем densityHmi.
        var massT = densityHmi > 0 ? volumeM3 * densityHmi * 0.001 : 0.0;

        return new LevelTankResult(hMaxMm, levelMm, volumeM3, massT);
    }
}

/// <summary>
/// Полный набор результатов одного расчёта LevelTank.
///
/// Эти четыре значения соответствуют старой Tank-структуре:
///
/// HMaxMm  -> *_H_MAX
/// LevelMm -> *_H_HMI
/// VolumeM3 -> *_V_HMI
/// MassT   -> *_M_HMI
/// </summary>
public sealed record LevelTankResult(int HMaxMm, int LevelMm, double VolumeM3, double MassT);