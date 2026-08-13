using TechMES.Calc.Tanks.Types;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Создаёт каталог алгоритмов, встроенных в текущую версию TechMES.Calc.
/// Каждый Tank Type является самостоятельным Calculation Definition.
///
/// Для добавления нового типа достаточно:
/// 1. создать TankTypeNVolumeDefinition.cs;
/// 2. реализовать его формулу;
/// 3. зарегистрировать его здесь.
/// </summary>
public static class BuiltInCalculationCatalog
{
    public static CalculationCatalog Create()
    {
        return new CalculationCatalog(
        [
            new TankType1VolumeDefinition(),
            new TankType2VolumeDefinition(),
            new TankType3VolumeDefinition(),
            new TankType4VolumeDefinition(),
            new TankType5VolumeDefinition(),
            new TankType6VolumeDefinition(),
            new TankType7VolumeDefinition(),
            new TankType8VolumeDefinition()
        ]);
    }
}