using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет общую валидацию, выполняемую базовым классом алгоритмов.
/// </summary>
public sealed class CalculationDefinitionBaseTests
{
    [Fact]
    public void AppliesDefaultParameterValue()
    {
        var definition = new MultiplyCalculationDefinition();

        var result = definition.Calculate(
            new CalculationParameterSet(
                new Dictionary<string, object?>
                {
                    ["value"] = 5.0
                }));

        Assert.True(result.IsSuccess);
        Assert.Equal(10.0, Assert.Single(result.Outputs).Value);
    }

    [Fact]
    public void ReturnsFailureForMissingParameter()
    {
        var definition = new MultiplyCalculationDefinition();

        var result = definition.Calculate(
            new CalculationParameterSet(
                new Dictionary<string, object?>()));

        Assert.False(result.IsSuccess);
        Assert.Equal("parameter.missing", result.ErrorCode);
    }

    [Fact]
    public void ReturnsFailureForUnknownParameter()
    {
        var definition = new MultiplyCalculationDefinition();

        var result = definition.Calculate(
            new CalculationParameterSet(
                new Dictionary<string, object?>
                {
                    ["value"] = 5.0,
                    ["unknown"] = 100.0
                }));

        Assert.False(result.IsSuccess);
        Assert.Equal("parameter.unknown", result.ErrorCode);
    }

    /// <summary>
    /// Простой тестовый алгоритм умножения.
    /// Он нужен только для проверки базовой инфраструктуры.
    /// </summary>
    private sealed class MultiplyCalculationDefinition : CalculationDefinitionBase
    {
        private static readonly IReadOnlyList<CalculationParameterDefinition>
            ParameterDefinitions =
            [
                new(
                    Key: "value",
                    Name: "Value",
                    Type: CalculationParameterType.Number,
                    IsRequired: true),

                new(
                    Key: "multiplier",
                    Name: "Multiplier",
                    Type: CalculationParameterType.Number,
                    IsRequired: false,
                    DefaultValue: 2.0)
            ];

        private static readonly IReadOnlyList<CalculationOutputDefinition>
            OutputDefinitions =
            [
                new(
                    Key: "result",
                    Name: "Result")
            ];

        public override string Code => "test.multiply";

        public override string Name => "Multiply";

        public override string Category => "Tests";

        public override string Version => "1";

        public override IReadOnlyList<CalculationParameterDefinition> Parameters =>
            ParameterDefinitions;

        public override IReadOnlyList<CalculationOutputDefinition> Outputs =>
            OutputDefinitions;

        protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
        {
            var value = parameters.GetRequiredDouble("value");
            var multiplier = parameters.GetRequiredDouble("multiplier");
            var result = value * multiplier;

            return CalculationResult.Success(
            [
                new CalculationOutput(
                    Key: "result",
                    Name: "Result",
                    Value: result)
            ]);
        }
    }
}