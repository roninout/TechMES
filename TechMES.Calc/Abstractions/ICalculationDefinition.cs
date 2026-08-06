using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Описывает один доступный тип расчёта.
///
/// Например:
/// tank.volume.rectangular
/// tank.mass
/// mixture.density
/// content.acn-water
///
/// Реализация содержит математический алгоритм, его коэффициенты,
/// параметры, выходы и правила валидации.
/// </summary>
public interface ICalculationDefinition
{
    /// <summary>
    /// Стабильный уникальный код алгоритма.
    /// </summary>
    string Code { get; }

    /// <summary>
    /// Отображаемое название алгоритма.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Категория для группировки в WEB и Maintenance.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Версия математического поведения алгоритма.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Перечень входных параметров.
    /// </summary>
    IReadOnlyList<CalculationParameterDefinition> Parameters { get; }

    /// <summary>
    /// Перечень выходных значений.
    /// </summary>
    IReadOnlyList<CalculationOutputDefinition> Outputs { get; }

    /// <summary>
    /// Выполняет расчёт.
    /// </summary>
    CalculationResult Calculate(CalculationParameterSet parameters, bool includeTrace = false);
}