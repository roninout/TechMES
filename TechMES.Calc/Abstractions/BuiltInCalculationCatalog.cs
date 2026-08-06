using TechMES.Calc.Tanks;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Создаёт каталог алгоритмов, встроенных в текущую версию TechMES.Calc.
///
/// Формулы и коэффициенты регистрируются здесь кодом,
/// а PostgreSQL в дальнейшем будет хранить только задания
/// и привязки параметров к тегам или константам.
/// </summary>
public static class BuiltInCalculationCatalog
{
    /// <summary>
    /// Создаёт новый неизменяемый набор доступных алгоритмов.
    /// </summary>
    public static CalculationCatalog Create()
    {
        return new CalculationCatalog(
        [
            new RectangularTankVolumeDefinition()
        ]);
    }
}