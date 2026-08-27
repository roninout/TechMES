using TechMES.Calc.Abstractions;
using TechMES.Calc.Capacity;
using TechMES.Calc.Constants;
using TechMES.Calc.Density;
using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет полноценные Calculation Definitions Density и Capacity.
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
    public void MixtureComponentOptionsExposeSubstancePhase()
    {
        var definition = new DensityCalculationDefinition();

        var componentParameter = definition.Parameters.Single(parameter => string.Equals(parameter.Key, "component0Code", StringComparison.OrdinalIgnoreCase));
        var water = componentParameter.Options!.Single(option => string.Equals(option.Value, "Water", StringComparison.OrdinalIgnoreCase));
        var waterVapor = componentParameter.Options!.Single(option => string.Equals(option.Value, "WaterS", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("liquid", water.Phase);
        Assert.Equal("vapor", waterVapor.Phase);
    }

    [Fact]
    public void CapacityComponentOptionsExposeOnlySupportedHeatCapacityModels()
    {
        var definition = new CapacityCalculationDefinition();

        var componentParameter = definition.Parameters.Single(parameter => string.Equals(parameter.Key, "component0Code", StringComparison.OrdinalIgnoreCase));
        var options = componentParameter.Options!;

        Assert.Contains(options, option => string.Equals(option.Value, "ACN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, option => string.Equals(option.Value, "DryMatter", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(options, option => string.Equals(option.Value, "Fusel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(options, option => string.Equals(option.Value, "Methan", StringComparison.OrdinalIgnoreCase));

        var acn = options.Single(option => string.Equals(option.Value, "ACN", StringComparison.OrdinalIgnoreCase));
        var acnVapor = options.Single(option => string.Equals(option.Value, "ACNS", StringComparison.OrdinalIgnoreCase));
        var dryMatter = options.Single(option => string.Equals(option.Value, "DryMatter", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("liquid", acn.Phase);
        Assert.Equal("vapor", acnVapor.Phase);
        Assert.Equal("liquid", dryMatter.Phase);
    }

    [Fact]
    public void DensityCalculatesPureAcetonitrile()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarGauge"] = 0d,
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
    public void DensityPressureIsOptionalAndDefaultsToZeroGauge()
    {
        var definition = new DensityCalculationDefinition();

        var pressure = definition.Parameters.Single(parameter =>
            string.Equals(parameter.Key, "pressureBarGauge", StringComparison.OrdinalIgnoreCase));

        Assert.False(pressure.IsRequired);
        Assert.Equal(0d, Convert.ToDouble(pressure.DefaultValue));
        Assert.Equal("bar(g)", pressure.Unit);
        Assert.Equal(CalculationParameterRole.ProcessInput, pressure.Role);
    }

    [Fact]
    public void DensityCanUseAtmosphericPressureWhenPressureInputIsNotConfigured()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 1,
                ["component0Code"] = "N",
                ["component0Percent"] = 100d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var expected = NitrogenDensity(
            temperatureC: 20d,
            pressureBarAbsolute: CalculationPhysicalConstants.AtmosphericPressureBarAbsolute);

        Assert.Equal(expected, GetOutput(result, "density"), precision: 10);
    }

    [Fact]
    public void DensityAddsGaugePressureToAtmosphericPressureBeforeCallingSubstanceFormula()
    {
        var definition = new DensityCalculationDefinition();

        const double pressureBarGauge = 1.7d;

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarGauge"] = pressureBarGauge,
                ["componentCount"] = 1,
                ["component0Code"] = "N",
                ["component0Percent"] = 100d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var expectedAbsolutePressure =
            pressureBarGauge + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

        var expected = NitrogenDensity(
            temperatureC: 20d,
            pressureBarAbsolute: expectedAbsolutePressure);

        Assert.Equal(expected, GetOutput(result, "density"), precision: 10);
    }

    [Fact]
    public void DensityAppliesEngineeringCorrectionWithoutLegacyScaling()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarGauge"] = 0d,
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
                ["pressureBarGauge"] = 0d,
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
    public void DensityReturnsComponentDensitiesForRuntimeVisualization()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["pressureBarGauge"] = 0d,
                ["componentCount"] = 2,

                ["component0Code"] = "ACN",
                ["component0Percent"] = 50d,

                ["component1Code"] = "Water",
                ["component1Percent"] = 50d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var component0Density = GetOutput(result, "component0Density");
        var component1Density = GetOutput(result, "component1Density");

        Assert.Equal(781.986d, component0Density, precision: 6);
        Assert.True(double.IsFinite(component1Density));
        Assert.True(component1Density > 0d);

        // Основной Density остаётся абсолютно тем же.
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
                ["pressureBarGauge"] = 1d,
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
    public void CapacityReturnsComponentHeatCapacitiesForRuntimeVisualization()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 2,

                ["component0Code"] = "ACN",
                ["component0Percent"] = 50d,

                ["component1Code"] = "Water",
                ["component1Percent"] = 50d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var component0Capacity = GetOutput(result, "component0Capacity");
        var component1Capacity = GetOutput(result, "component1Capacity");
        var mixtureCapacity = GetOutput(result, "capacity");

        Assert.Equal(2221.05154452d, component0Capacity, precision: 8);
        Assert.True(double.IsFinite(component1Capacity));
        Assert.True(component1Capacity > 0d);

        // Для массовых долей 50/50 итог обязан быть обычным
        // арифметическим средним чистых Cp компонентов.
        Assert.Equal(
            (component0Capacity + component1Capacity) * 0.5d,
            mixtureCapacity,
            precision: 8);
    }

    [Fact]
    public void CapacityDefinitionRejectsUnsupportedHeatCapacityModel()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 1,
                ["component0Code"] = "Fusel",
                ["component0Percent"] = 100d
            }));

        Assert.False(result.IsSuccess);
        Assert.Equal("parameter.selection-invalid", result.ErrorCode);
    }

    [Fact]
    public void ActiveComponentRequiresSubstanceCode()
    {
        var definition = new DensityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
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
            "pressureBarGauge",
            "additionalParameter1",
            "additionalParameter2",
            "additionalParameter3"
        ],
        processInputs);
    }

    [Fact]
    public void CapacityExposesFiveProcessInputs()
    {
        var definition = new CapacityCalculationDefinition();

        var processInputs = definition.Parameters
            .Where(parameter => parameter.Role == CalculationParameterRole.ProcessInput)
            .OrderBy(parameter => parameter.Order)
            .Select(parameter => parameter.Key)
            .ToArray();

        Assert.Equal(
        [
            "temperatureC",
            "pressureBarGauge",
            "additionalParameter1",
            "additionalParameter2",
            "additionalParameter3"
        ],
        processInputs);
    }

    private static double NitrogenDensity(double temperatureC, double pressureBarAbsolute)
    {
        const double gasConstant = 8.3144598d;
        const double molarMass = 28.0134d;

        // Оригинальный TechDotNetLib.GetDensity принимает float.
        // Expected обязан использовать тот же входной precision,
        // иначе тест проверяет уже другую, double-версию формулы.
        var legacyTemperature = (float)temperatureC;
        var legacyPressure = (float)pressureBarAbsolute;

        return legacyPressure * Math.Pow(10, 2)
            / (gasConstant / molarMass)
            / (legacyTemperature + 273.15);
    }

    private static double GetOutput(TechMES.Calc.Results.CalculationResult result, string key)
    {
        return result.Outputs.Single(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
