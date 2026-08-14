using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Tanks.Types;

namespace TechMES.Calc.Tests;

/// <summary>
/// Regression tests для полного legacy LevelTank -> Tank pipeline.
///
/// Геометрические expected Volume зафиксированы по рабочему
/// TechParamsCalc Tank.cs.
///
/// Дополнительно проверяем:
/// - H_MAX;
/// - преобразование Level.Val_R -> LevelMm;
/// - Mass через Density.ValHmi.
/// </summary>
public sealed class TankTypeVolumeDefinitionTests
{
    [Theory]
    [InlineData("tank.volume.type1", 8.203326737053668)]
    [InlineData("tank.volume.type2", 8.270689298743592)]
    [InlineData("tank.volume.type3", 4.812037329034269)]
    [InlineData("tank.volume.type4", 0.960000000000000)]
    [InlineData("tank.volume.type5", 5.372934717770584)]
    [InlineData("tank.volume.type6", 7.916813487046279)]
    [InlineData("tank.volume.type7", 8.236114359980968)]
    [InlineData("tank.volume.type8", 2.613569170367549)]
    public void CalculatesExpectedLegacyCompatibleVolume(
        string definitionCode,
        double expectedVolume)
    {
        var catalog = BuiltInCalculationCatalog.Create();
        var definition = catalog.GetRequired(definitionCode);
        var result = definition.Calculate(CreateParameters(definition));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var volume = result.Outputs.Single(output => output.Key == "volume");

        Assert.Equal("m³", volume.Unit);
        Assert.Equal(expectedVolume, volume.Value, precision: 10);
    }

    [Fact]
    public void CalculatesLegacyLevelTankOutputs()
    {
        var definition = new TankType4VolumeDefinition();
        var result = definition.Calculate(CreateParameters(definition));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(4, result.Outputs.Count);

        var hMax = result.Outputs.Single(output => output.Key == "hMax");
        var level = result.Outputs.Single(output => output.Key == "levelMm");
        var volume = result.Outputs.Single(output => output.Key == "volume");
        var mass = result.Outputs.Single(output => output.Key == "mass");

        /*
         * distanceB - distanceA = 1600 - 100 = 1500 mm
         */
        Assert.Equal(1500.0, hMax.Value, precision: 10);

        /*
         * (1500 * 666.667 * 10 / 10000) -> (int)1000.0005 -> 1000 mm
         */
        Assert.Equal(1000.0, level.Value, precision: 10);

        Assert.Equal(0.96, volume.Value, precision: 10);

        /*
         * 0.96 * 12000 * 0.0001 = 1.152 t
         */
        Assert.Equal(1.152, mass.Value, precision: 10);
    }

    [Fact]
    public void NegativeLevelRawProducesZeroLevel()
    {
        var definition = new TankType1VolumeDefinition();
        var parameters = CreateParameterValues(definition);

        parameters["levelRaw"] = -100.0;

        var result = definition.Calculate(new CalculationParameterSet(parameters));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var level = result.Outputs.Single(output => output.Key == "levelMm");

        Assert.Equal(0.0, level.Value, precision: 10);
    }

    [Fact]
    public void NonPositiveDensityProducesZeroMass()
    {
        var definition = new TankType1VolumeDefinition();
        var parameters = CreateParameterValues(definition);

        parameters["densityHmi"] = 0.0;

        var result = definition.Calculate(new CalculationParameterSet(parameters));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var mass = result.Outputs.Single(output => output.Key == "mass");

        Assert.Equal(0.0, mass.Value, precision: 10);
    }

    [Fact]
    public void BuiltInCatalogContainsAllEightTankTypes()
    {
        var catalog = BuiltInCalculationCatalog.Create();

        for (var type = 1; type <= 8; type++)
        {
            var definition = catalog.GetRequired($"tank.volume.type{type}");

            Assert.Equal("Tanks", definition.Category);
            Assert.Equal("2", definition.Version);
        }
    }

    [Fact]
    public void BuiltInCatalogContainsExactlyEightTankDefinitions()
    {
        var tankDefinitions = BuiltInCalculationCatalog.Create()
            .GetAll()
            .Where(definition =>
                string.Equals(
                    definition.Category,
                    "Tanks",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(8, tankDefinitions.Length);
    }

    [Fact]
    public void TypeFourUsesDistanceAInUnmeasuredBottomCalculation()
    {
        var definition = new TankType4VolumeDefinition();
        var result = definition.Calculate(CreateParameters(definition));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var volume = result.Outputs.Single(output => output.Key == "volume");

        Assert.Equal(0.96, volume.Value, precision: 10);
    }

    [Fact]
    public void ReturnsLevelTankTraceWhenRequested()
    {
        var definition = new TankType4VolumeDefinition();
        var result = definition.Calculate(
            CreateParameters(definition),
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(result.Trace, item => item.Key == "hMaxMm");
        Assert.Contains(result.Trace, item => item.Key == "levelMm");
        Assert.Contains(result.Trace, item => item.Key == "volumeM3");
        Assert.Contains(result.Trace, item => item.Key == "massT");
    }

    private static CalculationParameterSet CreateParameters(
        ICalculationDefinition definition)
    {
        return new CalculationParameterSet(CreateParameterValues(definition));
    }

    /// <summary>
    /// Stable regression vector.
    ///
    /// levelRaw = 666.667 специально выбран так, чтобы legacy-формула
    /// при DistanceA=100 / DistanceB=1600 дала ровно LevelMm=1000.
    /// </summary>
    private static Dictionary<string, object?> CreateParameterValues(
        ICalculationDefinition definition)
    {
        var values = new Dictionary<string, object?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in definition.Parameters)
        {
            values[parameter.Key] = parameter.Key switch
            {
                "levelRaw" => 666.667,
                "densityHmi" => 12000.0,

                "dimA" => 3000.0,
                "dimB" => 2000.0,
                "dimC" => 200.0,
                "dimD" => 1200.0,
                "dimE" => 400.0,
                "dimF" => 1000.0,

                "distanceA" => 100.0,
                "distanceB" => 1600.0,
                "distToDistanceA" => 100.0,
                "probeLength" => 2000.0,

                _ => throw new InvalidOperationException(
                    $"Unknown Tank test parameter '{parameter.Key}'.")
            };
        }

        return values;
    }
}