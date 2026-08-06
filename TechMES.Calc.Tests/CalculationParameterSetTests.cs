using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет универсальное чтение входных параметров.
/// </summary>
public sealed class CalculationParameterSetTests
{
    [Fact]
    public void ReadsValuesWithoutCaseSensitivity()
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperature"] = 80.5,
                ["enabled"] = "true",
                ["branch"] = "Low"
            });

        Assert.Equal(80.5, parameters.GetRequiredDouble("Temperature"));
        Assert.True(parameters.GetRequiredBoolean("ENABLED"));
        Assert.Equal("Low", parameters.GetRequiredString("branch"));
    }

    [Fact]
    public void ConvertsCommonNumericTypes()
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["intValue"] = 10,
                ["decimalValue"] = 12.5m,
                ["textValue"] = "14.75"
            });

        Assert.Equal(10, parameters.GetRequiredDouble("intValue"));
        Assert.Equal(12.5, parameters.GetRequiredDouble("decimalValue"));
        Assert.Equal(14.75, parameters.GetRequiredDouble("textValue"));
    }

    [Fact]
    public void ThrowsForMissingRequiredParameter()
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>());

        var exception = Assert.Throws<CalculationException>(
            () => parameters.GetRequiredDouble("pressure"));

        Assert.Equal("parameter.missing", exception.Code);
    }

    [Fact]
    public void RejectsNonFiniteNumber()
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperature"] = double.NaN
            });

        var exception = Assert.Throws<CalculationException>(
            () => parameters.GetRequiredDouble("temperature"));

        Assert.Equal("parameter.not-finite", exception.Code);
    }
}