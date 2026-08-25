using TechMES.Calc.Abstractions;
using TechMES.Calc.Capacity;
using TechMES.Calc.Density;
using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет уже не отдельные формулы Substance,
/// а полноценные Calculation Definitions Density и Capacity.
///
/// То есть здесь тестируется весь новый контракт:
/// - параметры CalculationDefinition;
/// - состав смеси;
/// - correction;
/// - DefaultValue;
/// - выходные инженерные единицы;
/// - регистрация в BuiltInCalculationCatalog.
/// </summary>
public sealed class DensityCapacityDefinitionTests
{
    [Fact]
    public void BuiltInCatalogContainsDensityAndCapacity()
    {
        var catalog = BuiltInCalculationCatalog.Create();

        Assert.IsType<DensityCalculationDefinition>(catalog.GetRequired(DensityCalculationDefinition.DefinitionCode));
        Assert.IsType<CapacityCalculationDefinition>(catalog.GetRequired(CapacityCalculationDefinition.DefinitionCode));
    }

    [Fact]
    public void DensityCalculatesPureAcetonitrile()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN",
                ["component0Percent"] = 100d
            }),
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(781.986d, GetOutput(result, "density"), precision: 6);
        Assert.NotEmpty(result.Trace);
    }

    [Fact]
    public void DensityPressureIsOptionalAndHasAtmosphericDefault()
    {
        var definition = new DensityCalculationDefinition();

        var pressure = definition.Parameters.Single(parameter =>
            string.Equals(parameter.Key, "pressureBarAbsolute", StringComparison.OrdinalIgnoreCase));

        Assert.False(pressure.IsRequired);
        Assert.Equal(1.01325d, Convert.ToDouble(pressure.DefaultValue));
        Assert.Equal(CalculationParameterRole.ProcessInput, pressure.Role);
    }

    [Fact]
    public void DensityCanUseDefaultPressureWhenPressureInputIsNotConfigured()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN",
                ["component0Percent"] = 100d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(781.986d, GetOutput(result, "density"), precision: 6);
    }

    [Fact]
    public void DensityAppliesEngineeringCorrectionWithoutLegacyScaling()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["densityCorrection"] = 2.5d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN",
                ["component0Percent"] = 100d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(784.486d, GetOutput(result, "density"), precision: 6);
    }

    [Fact]
    public void DensityCalculatesBinaryMixture()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["componentCount"] = 2,

                ["component0Code"] = "ACN",
                ["component0Percent"] = 50d,

                ["component1Code"] = "Water",
                ["component1Percent"] = 50d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(876.8993740276006d, GetOutput(result, "density"), precision: 8);
    }

    [Fact]
    public void CapacityCalculatesPureAcetonitrile()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN",
                ["component0Percent"] = 100d
            }),
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2221.05154452d, GetOutput(result, "capacity"), precision: 8);
        Assert.NotEmpty(result.Trace);
    }

    [Fact]
    public void CapacityCanUseDefaultPressureBecauseCurrentFormulaDoesNotDependOnPressure()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN",
                ["component0Percent"] = 100d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2221.05154452d, GetOutput(result, "capacity"), precision: 8);
    }

    [Fact]
    public void CapacityAppliesEngineeringCorrection()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["capacityCorrection"] = 100d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN",
                ["component0Percent"] = 100d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2321.05154452d, GetOutput(result, "capacity"), precision: 8);
    }

    [Fact]
    public void ActiveComponentRequiresSubstanceCode()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["componentCount"] = 2,

                ["component0Code"] = "ACN",
                ["component0Percent"] = 50d,

                ["component1Percent"] = 50d
            }));

        Assert.False(result.IsSuccess);
        Assert.Equal("mixture.component.code-missing", result.ErrorCode);
    }

    [Fact]
    public void ActiveComponentRequiresMassPercent()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["componentCount"] = 1,
                ["component0Code"] = "ACN"
            }));

        Assert.False(result.IsSuccess);
        Assert.Equal("mixture.component.percent-missing", result.ErrorCode);
    }

    [Fact]
    public void ComponentCountCannotExceedCurrentScadaStructure()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarAbsolute"] = 1d,
                ["componentCount"] = 6
            }));

        Assert.False(result.IsSuccess);
        Assert.Equal("parameter.above-maximum", result.ErrorCode);
    }

    [Fact]
    public void DensityExposesFiveProcessInputs()
    {
        var definition = new DensityCalculationDefinition();

        var processInputs = definition.Parameters
            .Where(parameter => parameter.Role == CalculationParameterRole.ProcessInput)
            .OrderBy(parameter => parameter.Order)
            .Select(parameter => parameter.Key)
            .ToArray();

        Assert.Equal(
        [
            "temperatureC",
            "pressureBarAbsolute",
            "additionalParameter1",
            "additionalParameter2",
            "additionalParameter3"
        ],
        processInputs);
    }

    [Fact]
    public void CapacityExposesTemperatureAndPressureAsProcessInputs()
    {
        var definition = new CapacityCalculationDefinition();

        var processInputs = definition.Parameters
            .Where(parameter => parameter.Role == CalculationParameterRole.ProcessInput)
            .OrderBy(parameter => parameter.Order)
            .Select(parameter => parameter.Key)
            .ToArray();

        Assert.Equal(["temperatureC", "pressureBarAbsolute"], processInputs);
    }

    private static double GetOutput(TechMES.Calc.Results.CalculationResult result, string key)
    {
        return result.Outputs.Single(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}