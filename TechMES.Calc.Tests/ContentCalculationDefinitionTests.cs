using TechMES.Calc.Abstractions;
using TechMES.Calc.Content;
using TechMES.Calc.Parameters;
using TechMES.Calc.Constants;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет интеграцию production Content-корреляций
/// с общей инфраструктурой Calculation Definition.
/// </summary>
public sealed class ContentCalculationDefinitionTests
{
    [Fact]
    public void BuiltInCatalogContainsAllContentDefinitions()
    {
        var catalog = BuiltInCalculationCatalog.Create();

        var actualCodes = catalog.GetAll()
            .Where(definition => definition.Category == "Content")
            .Select(definition => definition.Code)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        var expectedCodes = new[]
        {
            ContentCalculationDefinitions.AcaPoCode,
            ContentCalculationDefinitions.AcnWaterCode,
            ContentCalculationDefinitions.AcnWaterPoCode,
            ContentCalculationDefinitions.AlcWaterCode,
            ContentCalculationDefinitions.PoPropyleneCode,
            ContentCalculationDefinitions.PoWaterCode
        }
        .OrderBy(code => code, StringComparer.Ordinal)
        .ToArray();

        Assert.Equal(expectedCodes, actualCodes);
    }

    [Fact]
    public void ContentDefinitionsExposeExpectedParameterContract()
    {
        var definitions = BuiltInCalculationCatalog.Create().GetAll()
            .Where(definition => definition.Category == "Content")
            .ToArray();

        Assert.Equal(6, definitions.Length);

        foreach (var definition in definitions)
        {
            Assert.Equal(14, definition.Parameters.Count);

            var temperature = Assert.Single(definition.Parameters, parameter => parameter.Key == "temperatureC");

            Assert.Equal(CalculationParameterType.Number, temperature.Type);
            Assert.Equal(CalculationParameterRole.ProcessInput, temperature.Role);
            Assert.Equal("°C", temperature.Unit);

            var pressure = Assert.Single(definition.Parameters, parameter => parameter.Key == "pressureBarGauge");

            Assert.Equal(CalculationParameterType.Number, pressure.Type);
            Assert.Equal(CalculationParameterRole.ProcessInput, pressure.Role);
            Assert.Equal("bar(g)", pressure.Unit);

            var configuration = Assert.Single(definition.Parameters, parameter => parameter.Key == "configurationCode");

            Assert.Equal(CalculationParameterType.Integer, configuration.Type);
            Assert.Equal(CalculationParameterRole.Configuration, configuration.Role);
        }
    }

    [Theory]
    [InlineData(ContentCalculationDefinitions.AcnWaterCode, 80d, 1.0d, 10)]
    [InlineData(ContentCalculationDefinitions.PoPropyleneCode, 80d, 1.45d, 10)]
    [InlineData(ContentCalculationDefinitions.PoWaterCode, 70d, 1.25d, 10)]
    [InlineData(ContentCalculationDefinitions.AcaPoCode, 50d, 0.725d, 10)]
    [InlineData(ContentCalculationDefinitions.AlcWaterCode, 80d, 1.0d, 10)]
    [InlineData(ContentCalculationDefinitions.AcnWaterPoCode, 60d, 1.0d, 20)]
    public void ContentDefinitionMatchesContentFacade(string definitionCode, double temperatureC, double pressureBarGauge, int configurationCode)
    {
        var definition = BuiltInCalculationCatalog.Create().GetRequired(definitionCode);
        var components = GetComponents(definitionCode);
        var pressureBarAbsolute = pressureBarGauge + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

        var expected = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
            Components: components,
            TemperatureC: temperatureC,
            PressureBarAbsolute: pressureBarAbsolute,
            ConfigurationCode: configurationCode));

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = temperatureC,
                ["pressureBarGauge"] = pressureBarGauge,
                ["configurationCode"] = configurationCode,
                ["selectedContentItemIndex"] = 0
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(expected.Count, result.Outputs.Count);

        for (var index = 0; index < expected.Count; index++)
            Assert.Equal(expected[index], result.Outputs[index].Value, precision: 10);
    }

    [Fact]
    public void ContentDefinitionConvertsExpectedCalculationErrorToFailure()
    {
        var definition = BuiltInCalculationCatalog.Create()
            .GetRequired(ContentCalculationDefinitions.AcnWaterCode);

        var result = definition.Calculate(
            new CalculationParameterSet(
                new Dictionary<string, object?>
                {
                    ["temperatureC"] = 80d,
                    ["pressureBarGauge"] = 0d,
                    ["configurationCode"] = 30,
                    ["selectedContentItemIndex"] = 0
                }));

        Assert.False(result.IsSuccess);
        Assert.Equal("content.configuration.unsupported", result.ErrorCode);
    }

    private static string[] GetComponents(string definitionCode)
    {
        return definitionCode switch
        {
            ContentCalculationDefinitions.AcnWaterCode => ["ACN", "Water"],
            ContentCalculationDefinitions.PoPropyleneCode => ["PO", "P"],
            ContentCalculationDefinitions.PoWaterCode => ["PO", "Water"],
            ContentCalculationDefinitions.AcaPoCode => ["ACA", "PO"],
            ContentCalculationDefinitions.AlcWaterCode => ["ALC", "Water"],
            ContentCalculationDefinitions.AcnWaterPoCode => ["ACN", "Water", "PO"],

            _ => throw new InvalidOperationException(
                $"Unknown Content definition '{definitionCode}'.")
        };
    }

    [Fact]
    public void AlcoholWaterContentMatchesLegacyControllerAtZeroGaugePressure()
    {
        var definition = BuiltInCalculationCatalog.Create().GetRequired(ContentCalculationDefinitions.AlcWaterCode);

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 36.1d,
                ["pressureBarGauge"] = 0d,
                ["configurationCode"] = 11,
                ["selectedContentItemIndex"] = 0
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var alcohol = Assert.Single(result.Outputs, output => output.Key == "alcPercent");
        var water = Assert.Single(result.Outputs, output => output.Key == "waterPercent");

        Assert.Equal(100.105351337d, alcohol.Value, precision: 9);
        Assert.Equal(-0.105351337d, water.Value, precision: 9);
    }

    [Fact]
    public void ContentDefinitionAppliesOnlySelectedComponentDeltas()
    {
        var definition = BuiltInCalculationCatalog.Create().GetRequired(ContentCalculationDefinitions.AlcWaterCode);

        const double temperatureC = 36.1d;
        const double pressureBarGauge = 0.32d;
        const double pressureDeltaBar = 0.08d;
        const double temperatureDeltaC = 1.5d;
        const int configurationCode = 11;

        var expected = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
            Components: ["ALC", "Water"],
            TemperatureC: temperatureC + temperatureDeltaC,
            PressureBarAbsolute: pressureBarGauge + pressureDeltaBar + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute,
            ConfigurationCode: configurationCode));

        var result = definition.Calculate(new CalculationParameterSet(new Dictionary<string, object?>
        {
            ["temperatureC"] = temperatureC,
            ["pressureBarGauge"] = pressureBarGauge,
            ["configurationCode"] = configurationCode,
            ["selectedContentItemIndex"] = 0,
            ["component0PressureDelta"] = pressureDeltaBar,
            ["component0TemperatureDelta"] = temperatureDeltaC,

            // Эти значения принадлежат Water и при Select=0
            // не должны участвовать в расчёте.
            ["component1PressureDelta"] = 5d,
            ["component1TemperatureDelta"] = 50d
        }));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(expected.Count, result.Outputs.Count);

        for (var index = 0; index < expected.Count; index++)
            Assert.Equal(expected[index], result.Outputs[index].Value, precision: 10);
    }
}