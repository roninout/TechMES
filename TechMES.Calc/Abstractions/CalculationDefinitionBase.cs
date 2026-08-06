using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Abstractions;

/// <summary>
/// Базовый класс для всех расчётных алгоритмов.
///
/// Он централизованно:
/// - применяет значения параметров по умолчанию;
/// - проверяет обязательные параметры;
/// - проверяет типы и диапазоны;
/// - отклоняет неизвестные параметры;
/// - преобразует ожидаемые CalculationException в CalculationResult.
///
/// Конкретный алгоритм реализует только CalculateCore.
/// </summary>
public abstract class CalculationDefinitionBase : ICalculationDefinition
{
    public abstract string Code { get; }

    public abstract string Name { get; }

    public abstract string Category { get; }

    public abstract string Version { get; }

    public abstract IReadOnlyList<CalculationParameterDefinition> Parameters { get; }

    public abstract IReadOnlyList<CalculationOutputDefinition> Outputs { get; }

    /// <summary>
    /// Выполняет общую подготовку и затем вызывает конкретный алгоритм.
    /// </summary>
    public CalculationResult Calculate(CalculationParameterSet parameters, bool includeTrace = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        try
        {
            var validatedParameters = CalculationParameterValidator.CreateValidatedSet(Parameters, parameters);

            return CalculateCore(validatedParameters, includeTrace);
        }
        catch (CalculationException exception)
        {
            // Ошибки входных данных являются ожидаемым результатом,
            // поэтому они не должны аварийно останавливать Calc.Service.
            return CalculationResult.Failure(exception.Code, exception.Message);
        }
    }

    /// <summary>
    /// Выполняет конкретную математическую реализацию.
    ///
    /// Все входные параметры к этому моменту уже проверены
    /// и дополнены значениями по умолчанию.
    /// </summary>
    protected abstract CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace);
}