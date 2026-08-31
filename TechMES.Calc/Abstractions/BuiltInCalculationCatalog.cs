using TechMES.Calc.Capacity;
using TechMES.Calc.Content;
using TechMES.Calc.Density;
using TechMES.Calc.Tanks.Types;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Создаёт каталог всех алгоритмов, встроенных в текущую версию TechMES.Calc.
///
/// В каталог входят:
/// - Tank Type 1..8;
/// - Density многокомпонентной смеси;
/// - Capacity многокомпонентной смеси;
/// - все поддерживаемые Content-системы.
///
/// Добавление нового Calculation Definition не требует изменения
/// CalculationCatalog, Runtime или PostgreSQL.
/// Достаточно реализовать ICalculationDefinition и зарегистрировать его здесь.
/// </summary>
public static class BuiltInCalculationCatalog
{
    public static CalculationCatalog Create()
    {
        var definitions = new List<ICalculationDefinition>
        {
            new TankType1VolumeDefinition(),
            new TankType2VolumeDefinition(),
            new TankType3VolumeDefinition(),
            new TankType4VolumeDefinition(),
            new TankType5VolumeDefinition(),
            new TankType6VolumeDefinition(),
            new TankType7VolumeDefinition(),
            new TankType8VolumeDefinition(),

            new DensityCalculationDefinition(),
            new CapacityCalculationDefinition()
        };

        definitions.AddRange(ContentCalculationDefinitions.CreateAll());

        return new CalculationCatalog(definitions);
    }
}