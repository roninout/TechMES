using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет регистрацию и поиск алгоритмов.
/// </summary>
public sealed class CalculationCatalogTests
{
    [Fact]
    public void FindsDefinitionWithoutCaseSensitivity()
    {
        var catalog = new CalculationCatalog(
        [
            new TestCalculationDefinition("tank.volume.rectangular")
        ]);

        var definition =
            catalog.GetRequired("TANK.VOLUME.RECTANGULAR");

        Assert.Equal("tank.volume.rectangular", definition.Code);
    }

    [Fact]
    public void RejectsDuplicateDefinitionCodes()
    {
        var exception = Assert.Throws<CalculationException>(
            () => new CalculationCatalog(
            [
                new TestCalculationDefinition("tank.volume"),
                new TestCalculationDefinition("tank.volume")
            ]));

        Assert.Equal("definition.duplicate", exception.Code);
    }

    [Fact]
    public void RejectsInvalidDefinitionCode()
    {
        var exception = Assert.Throws<CalculationException>(
            () => new CalculationCatalog(
            [
                new TestCalculationDefinition("Tank Volume")
            ]));

        Assert.Equal("definition.code-invalid", exception.Code);
    }

    /// <summary>
    /// Минимальная тестовая реализация алгоритма.
    /// </summary>
    private sealed class TestCalculationDefinition(string code) : CalculationDefinitionBase
    {
        public override string Code { get; } = code;

        public override string Name => "Test calculation";

        public override string Category => "Tests";

        public override string Version => "1";

        public override IReadOnlyList<CalculationParameterDefinition> Parameters => Array.Empty<CalculationParameterDefinition>();

        public override IReadOnlyList<CalculationOutputDefinition> Outputs => Array.Empty<CalculationOutputDefinition>();

        protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
        {
            return CalculationResult.Success(Array.Empty<CalculationOutput>());
        }
    }
}