using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Tanks;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет универсальное определение Tank Volume,
/// которое позже будет вызываться из Runtime и WEB-тестера.
/// </summary>
public sealed class RectangularTankVolumeDefinitionTests
{
    [Fact]
    public void CalculatesVolumeAndReturnsTrace()
    {
        var definition = new RectangularTankVolumeDefinition();

        var result = definition.Calculate(
            CreateParameters(levelMm: 1500),
            includeTrace: true);

        Assert.True(result.IsSuccess);

        var output = Assert.Single(result.Outputs);

        Assert.Equal("volume", output.Key);
        Assert.Equal(12.0, output.Value, 10);
        Assert.NotEmpty(result.Trace);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void NormalizesNegativeLevelAndReturnsWarning()
    {
        var definition = new RectangularTankVolumeDefinition();

        var result = definition.Calculate(
            CreateParameters(levelMm: -100));

        Assert.True(result.IsSuccess);
        Assert.Equal(3.0, Assert.Single(result.Outputs).Value, 10);

        Assert.Contains(
            result.Messages,
            message => message.Code == "tank.level-below-zero");
    }

    [Fact]
    public void WarnsWhenEffectiveHeightExceedsTankHeight()
    {
        var definition = new RectangularTankVolumeDefinition();

        var result = definition.Calculate(
            CreateParameters(levelMm: 4000));

        Assert.True(result.IsSuccess);

        Assert.Contains(
            result.Messages,
            message => message.Code == "tank.level-above-height");
    }

    [Fact]
    public void ConvertsGeometryErrorToFailureResult()
    {
        var parameters = CreateParameters(levelMm: 1000);

        var values = new Dictionary<string, object?>(
            parameters.Values,
            StringComparer.OrdinalIgnoreCase)
        {
            ["distanceAMm"] = 1000.0,
            ["distanceBMm"] = 500.0,
            ["distanceToPointAMm"] = 1500.0
        };

        var definition = new RectangularTankVolumeDefinition();

        var result = definition.Calculate(
            new CalculationParameterSet(values));

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "tank.measurement.distance-order-invalid",
            result.ErrorCode);
    }

    [Fact]
    public void BuiltInCatalogContainsRectangularTank()
    {
        var catalog = BuiltInCalculationCatalog.Create();

        var definition =
            catalog.GetRequired("tank.volume.rectangular");

        Assert.IsType<RectangularTankVolumeDefinition>(definition);
        Assert.Equal("1", definition.Version);
    }

    /// <summary>
    /// Создаёт параметры, соответствующие старой модели Tank Type 4.
    /// </summary>
    private static CalculationParameterSet CreateParameters(
        double levelMm)
    {
        return new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelMm"] = levelMm,
                ["heightMm"] = 4000.0,
                ["widthMm"] = 2000.0,
                ["lengthMm"] = 3000.0,
                ["distanceToPointAMm"] = 500.0,
                ["distanceAMm"] = 100.0,
                ["distanceBMm"] = 3100.0
            });
    }
}