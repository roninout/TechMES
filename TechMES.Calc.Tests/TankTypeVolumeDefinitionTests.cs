using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Tanks.Types;

namespace TechMES.Calc.Tests;

/// <summary>
/// Regression tests для восьми рабочих Tank Type algorithms.
///
/// Значения ожидаемого объёма зафиксированы по математике,
/// перенесённой из рабочего TechParamsCalc Tank.cs.
///
/// Если в дальнейшем мы сознательно корректируем формулу конкретного
/// типа, соответствующее ожидаемое значение меняем только после проверки.
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
    public void CalculatesExpectedLegacyCompatibleVolume(string definitionCode, double expectedVolume)
    {
        var catalog = BuiltInCalculationCatalog.Create();
        var definition = catalog.GetRequired(definitionCode);
        var result = definition.Calculate(CreateParameters(definition));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var output = Assert.Single(result.Outputs);

        Assert.Equal("volume", output.Key);
        Assert.Equal("m³", output.Unit);
        Assert.Equal(expectedVolume, output.Value, precision: 10);
    }

    [Fact]
    public void BuiltInCatalogContainsAllEightTankTypes()
    {
        var catalog = BuiltInCalculationCatalog.Create();

        for (var type = 1; type <= 8; type++)
        {
            var definition = catalog.GetRequired($"tank.volume.type{type}");

            Assert.Equal("Tanks", definition.Category);
            Assert.Equal("1", definition.Version);
        }
    }

    [Fact]
    public void BuiltInCatalogContainsExactlyEightTankDefinitions()
    {
        var tankDefinitions = BuiltInCalculationCatalog.Create()
            .GetAll()
            .Where(definition => string.Equals(definition.Category, "Tanks", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(8, tankDefinitions.Length);
    }

    [Fact]
    public void TypeFourUsesDistanceAInUnmeasuredBottomCalculation()
    {
        var definition = new TankType4VolumeDefinition();

        var parameters = new CalculationParameterSet(new Dictionary<string, object?>
        {
            ["levelMm"] = 1000.0,
            ["dimA"] = 3000.0,
            ["dimB"] = 2000.0,
            ["dimC"] = 200.0,
            ["distanceA"] = 100.0,
            ["distanceB"] = 1600.0,
            ["distToDistanceA"] = 100.0,
            ["probeLength"] = 2000.0
        });

        var result = definition.Calculate(parameters);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(0.96, Assert.Single(result.Outputs).Value, precision: 10);
    }

    [Fact]
    public void ReturnsTraceWhenRequested()
    {
        var definition = new TankType4VolumeDefinition();
        var result = definition.Calculate(CreateParameters(definition), includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotEmpty(result.Trace);
        Assert.Contains(result.Trace, item => item.Key == "volumeM3");
    }

    /// <summary>
    /// Формирует один стабильный regression-набор параметров.
    ///
    /// Definition получает только те dim-параметры,
    /// которые реально поддерживает конкретный Tank Type.
    /// </summary>
    private static CalculationParameterSet CreateParameters(ICalculationDefinition definition)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in definition.Parameters)
        {
            values[parameter.Key] = parameter.Key switch
            {
                "levelMm" => 1000.0,

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

                _ => throw new InvalidOperationException($"Unknown Tank test parameter '{parameter.Key}'.")
            };
        }

        return new CalculationParameterSet(values);
    }
}