using TechMES.Calc.Tanks;
using TechMES.Calc.Tanks.Types;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Создаёт каталог алгоритмов,
/// встроенных в текущую версию TechMES.Calc.
///
/// Формулы и коэффициенты находятся в TechMES.Calc,
/// а PostgreSQL хранит только Jobs и привязки параметров.
/// </summary>
public static class BuiltInCalculationCatalog
{
    /// <summary>
    /// Создаёт неизменяемый набор доступных алгоритмов.
    /// </summary>
    public static CalculationCatalog Create()
    {
        return new CalculationCatalog(
        [
            /*
             * Старый rectangular Definition оставляем.
             *
             * Это важно для обратной совместимости,
             * если в PostgreSQL уже существует Job:
             *
             * DefinitionCode = tank.volume.rectangular
             */
            new RectangularTankVolumeDefinition(),

            
            // Новые production Tank Types.
            new TankType1VolumeDefinition(),
            new TankType2VolumeDefinition(),
            new TankType3VolumeDefinition(),
            new TankType4VolumeDefinition(),
            new TankType5VolumeDefinition()
        ]);
    }
}