namespace TechMES.Calc.Tanks.Legacy;

/// <summary>
/// Содержит неизменённую математическую реализацию Tank Type 4
/// из старого проекта TechParamsCalc.
///
/// Этот класс используется как эталон при переносе и не должен
/// применяться в новой рабочей конфигурации Calc.Service.
/// </summary>
public static class LegacyRectangularTankVolumeCalculator
{
    /// <summary>
    /// Рассчитывает объём прямоугольного резервуара в кубических метрах.
    ///
    /// Порядок операций специально сохранён таким же, как в старом
    /// методе Tank.GetTypeFourVolume.
    /// </summary>
    public static double Calculate(
        int levelMm,
        int heightMm,
        int widthMm,
        int lengthMm,
        int distanceToPointAMm,
        int distanceAMm,
        int distanceBMm)
    {
        // В старой реализации dimA являлся полной высотой резервуара.
        var totalLength = heightMm;

        // Определяем высоту жидкости ниже начала измеряемого диапазона.
        var levelFromSensorToBottomOfTheTank = Math.Max(
            0,
            totalLength
            - (distanceToPointAMm + (distanceBMm - distanceAMm)));

        // Объём жидкости, находящейся ниже измеряемого диапазона датчика.
        var volumeLeft =
            widthMm * 0.001
            * lengthMm * 0.001
            * levelFromSensorToBottomOfTheTank * 0.001;

        // Объём жидкости, рассчитанный по текущему показанию уровня.
        var volumeLevel =
            widthMm * 0.001
            * lengthMm * 0.001
            * Math.Max(0, levelMm) * 0.001;

        // Старый алгоритм не ограничивал результат полной высотой резервуара.
        return volumeLevel + Math.Max(0, volumeLeft);
    }
}