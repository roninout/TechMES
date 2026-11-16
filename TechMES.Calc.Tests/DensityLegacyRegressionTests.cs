using TechMES.Calc.Constants;
using TechMES.Calc.Density;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tests;

/// <summary>
/// Проверяет legacy-формулы веществ и смеси с текущей атмосферной поправкой TechMES.
/// Ожидаемые значения вычисляются независимо от MixturePropertyCalculator.
/// Полное численное совпадение со старой программой требует также одинакового Patm.
/// </summary>
public sealed class DensityLegacyRegressionTests
{
    private const double LegacyGasConstant = 8.3144598d;
    private const double AtmosphericPressureBarAbsolute = CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

    /// <summary>Проверяет чистый жидкий ACN, плотность которого не зависит от давления.</summary>
    [Fact]
    public void PureAcetonitrileMatchesLegacyCalculation()
    {
        const double temperatureC = 20d;

        var expectedDensity = LegacyAcetonitrileLiquidDensity(temperatureC);
        var actualDensity = CalculateDensity(temperatureC, 0d, 0d, [new("ACN", 100d)]);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    /// <summary>Отсутствующий Pressure означает 0 bar(g), а абсолютное давление равно текущему Patm.</summary>
    [Fact]
    public void NitrogenWithoutPressureTagUsesAtmosphericAbsolutePressure()
    {
        const double temperatureC = 20d;

        var expectedDensity = LegacyIdealGasDensity(temperatureC, AtmosphericPressureBarAbsolute, 28.0134d);
        var actualDensity = CalculateDensity(temperatureC, null, 0d, [new("N", 100d)]);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    /// <summary>Проверяет добавление текущего Patm к избыточному давлению до legacy-формулы азота.</summary>
    [Fact]
    public void NitrogenAddsAtmosphericPressureToGaugePressure()
    {
        const double temperatureC = 20d;
        const double pressureBarGauge = 1.7d;

        var pressureBarAbsolute = pressureBarGauge + AtmosphericPressureBarAbsolute;
        var expectedDensity = LegacyIdealGasDensity(temperatureC, pressureBarAbsolute, 28.0134d);
        var actualDensity = CalculateDensity(temperatureC, pressureBarGauge, 0d, [new("N", 100d)]);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    /// <summary>Проверяет газовую смесь: текущий Patm, legacy-плотности, массовые доли и DeltaD.</summary>
    [Fact]
    public void NitrogenOxygenMixtureWithPressureAndDeltaDMatchesLegacyCalculation()
    {
        const double temperatureC = 35d;
        const double pressureBarGauge = 2.4d;
        const double deltaD = 0.8d;
        const double nitrogenPercent = 65d;
        const double oxygenPercent = 35d;

        var pressureBarAbsolute = pressureBarGauge + AtmosphericPressureBarAbsolute;

        var nitrogenDensity = LegacyIdealGasDensity(temperatureC, pressureBarAbsolute, 28.0134d);
        var oxygenDensity = LegacyIdealGasDensity(temperatureC, pressureBarAbsolute, 31.998d);

        var expectedBaseDensity = LegacyMixtureDensity(
        [
            new(nitrogenDensity, nitrogenPercent),
            new(oxygenDensity, oxygenPercent)
        ]);

        var expectedFinalDensity = expectedBaseDensity + deltaD;

        var actualDensity = CalculateDensity(
            temperatureC,
            pressureBarGauge,
            deltaD,
            [
                new("N", nitrogenPercent),
                new("O2", oxygenPercent)
            ]);

        Assert.Equal(expectedFinalDensity, actualDensity, precision: 10);
    }

    /// <summary>DeltaD добавляется в kg/m³; масштабирование SCADA не входит в Calculation Definition.</summary>
    [Fact]
    public void DeltaDIsAddedInEngineeringUnitsBeforeScadaScaling()
    {
        const double temperatureC = 20d;
        const double deltaD = 2.5d;

        var expectedBaseDensity = LegacyAcetonitrileLiquidDensity(temperatureC);
        var expectedFinalDensity = expectedBaseDensity + deltaD;
        var actualDensity = CalculateDensity(temperatureC, 0d, deltaD, [new("ACN", 100d)]);

        Assert.Equal(expectedFinalDensity, actualDensity, precision: 10);
        Assert.Equal(784.486d, actualDensity, precision: 6);
    }

    /// <summary>Выполняет настоящий DensityCalculationDefinition; null исключает Pressure из входов.</summary>
    private static double CalculateDensity(double temperatureC, double? pressureBarGauge, double deltaD, IReadOnlyList<LegacyComponent> components)
    {
        var values = new Dictionary<string, object?>
        {
            ["temperatureC"] = temperatureC,
            ["densityCorrection"] = deltaD,
            ["componentCount"] = components.Count
        };

        if (pressureBarGauge.HasValue)
            values["pressureBarGauge"] = pressureBarGauge.Value;

        for (var index = 0; index < components.Count; index++)
        {
            values[$"component{index}Code"] = components[index].Code;
            values[$"component{index}Percent"] = components[index].MassPercent;
        }

        var definition = new DensityCalculationDefinition();
        var result = definition.Calculate(new CalculationParameterSet(values), includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        return GetOutput(result, "density");
    }

    /// <summary>Исходная формула ACN с преобразованием температуры во float, как в legacy.</summary>
    private static double LegacyAcetonitrileLiquidDensity(double temperatureC)
    {
        var temperature = (float)temperatureC;

        const double a0 = 803.07d;
        const double a1 = -1.0542d;

        return a1 * temperature + a0;
    }

    /// <summary>Legacy-формула идеального газа; оба входа сначала приводятся к float.</summary>
    private static double LegacyIdealGasDensity(double temperatureC, double pressureBarAbsolute, double molarMass)
    {
        var temperature = (float)temperatureC;
        var pressure = (float)pressureBarAbsolute;

        return pressure * Math.Pow(10d, 2d) / (LegacyGasConstant / molarMass) / (temperature + 273.15d);
    }

    /// <summary>Независимо повторяет legacy-смешение rhoMix = 1 / Sum(w_i / rho_i).</summary>
    private static double LegacyMixtureDensity(IReadOnlyList<LegacyPureDensity> components)
    {
        var denominator = 0d;

        foreach (var component in components)
            denominator += component.MassPercent * 0.01d / component.DensityKgPerM3;

        return 1d / denominator;
    }

    /// <summary>Возвращает инженерное значение именованного выхода расчёта.</summary>
    private static double GetOutput(CalculationResult result, string key) => result.Outputs.Single(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed record LegacyComponent(string Code, double MassPercent);
    private sealed record LegacyPureDensity(double DensityKgPerM3, double MassPercent);
}