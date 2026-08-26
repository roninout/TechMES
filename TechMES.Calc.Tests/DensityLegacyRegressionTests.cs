using TechMES.Calc.Constants;
using TechMES.Calc.Density;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tests;

/// <summary>
/// Контрольные OLD -> NEW тесты для полного расчёта Density.
///
/// Эти тесты намеренно не используют MixturePropertyCalculator
/// для вычисления ожидаемого значения.
///
/// Левая часть каждого теста вручную повторяет математическую цепочку
/// старого TechParamsCalc + TechDotNetLib:
///
/// 1. Pressure из SCADA является избыточным.
/// 2. Формируется абсолютное давление:
///        P(abs) = P(g) + Patm.
/// 3. Вычисляется плотность каждого вещества по исходной формуле.
/// 4. Смесь считается по старой формуле:
///        rhoMix = 1 / Sum(w_i / rho_i).
/// 5. К инженерной плотности добавляется DeltaD.
///
/// Правая часть выполняется новым DensityCalculationDefinition.
///
/// Поэтому тесты контролируют не только отдельную формулу вещества,
/// но и правильность всей новой Density-обвязки.
/// </summary>
public sealed class DensityLegacyRegressionTests
{
    private const double LegacyGasConstant = 8.3144598d;

    /// <summary>
    /// Проверяет самый простой жидкостный вариант.
    ///
    /// Для жидкого ACN Pressure в исходной формуле не используется.
    /// Этот тест нужен как базовая контрольная точка:
    /// Temperature -> pure substance -> Density.
    /// </summary>
    [Fact]
    public void PureAcetonitrileMatchesLegacyCalculation()
    {
        const double temperatureC = 20d;

        var expectedDensity = LegacyAcetonitrileLiquidDensity(temperatureC);

        var actualDensity = CalculateDensity(
            temperatureC: temperatureC,
            pressureBarGauge: 0d,
            deltaD: 0d,
            components:
            [
                new LegacyComponent("ACN", 100d)
            ]);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    /// <summary>
    /// Проверяет режим без подключённого Pressure.
    ///
    /// В старой программе отсутствие Pressure означало нулевое
    /// избыточное давление.
    ///
    /// В новом Density используется тот же контракт:
    ///
    ///     P(g) = 0
    ///     P(abs) = 0 + 1.01325
    ///
    /// Азот выбран специально, потому что его Density непосредственно
    /// зависит от абсолютного давления.
    /// </summary>
    [Fact]
    public void NitrogenWithoutPressureTagUsesAtmosphericAbsolutePressure()
    {
        const double temperatureC = 20d;

        var pressureBarAbsolute = CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

        var expectedDensity = LegacyIdealGasDensity(
            temperatureC: temperatureC,
            pressureBarAbsolute: pressureBarAbsolute,
            molarMass: 28.0134d);

        var actualDensity = CalculateDensity(
            temperatureC: temperatureC,
            pressureBarGauge: null,
            deltaD: 0d,
            components:
            [
                new LegacyComponent("N", 100d)
            ]);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    /// <summary>
    /// Проверяет ключевую legacy-логику Pressure.
    ///
    /// В SCADA приходит избыточное давление 1.7 bar(g).
    ///
    /// Перед GetDensity старый TechParamsCalc формировал:
    ///
    ///     1.7 + 1.01325 = 2.71325 bar(abs)
    ///
    /// Именно 2.71325 bar(abs) должно попасть в формулу азота.
    /// </summary>
    [Fact]
    public void NitrogenAddsAtmosphericPressureToGaugePressure()
    {
        const double temperatureC = 20d;
        const double pressureBarGauge = 1.7d;

        var pressureBarAbsolute =
            pressureBarGauge
            + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

        var expectedDensity = LegacyIdealGasDensity(
            temperatureC: temperatureC,
            pressureBarAbsolute: pressureBarAbsolute,
            molarMass: 28.0134d);

        var actualDensity = CalculateDensity(
            temperatureC: temperatureC,
            pressureBarGauge: pressureBarGauge,
            deltaD: 0d,
            components:
            [
                new LegacyComponent("N", 100d)
            ]);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    /// <summary>
    /// Проверяет полный газовый расчёт:
    ///
    /// - Temperature;
    /// - gauge -> absolute Pressure;
    /// - два компонента;
    /// - массовые проценты;
    /// - legacy harmonic-volume mixture formula;
    /// - DeltaD.
    ///
    /// Здесь уже проверяется практически вся математическая цепочка Density.
    /// </summary>
    [Fact]
    public void NitrogenOxygenMixtureWithPressureAndDeltaDMatchesLegacyCalculation()
    {
        const double temperatureC = 35d;
        const double pressureBarGauge = 2.4d;
        const double deltaD = 0.8d;

        const double nitrogenPercent = 65d;
        const double oxygenPercent = 35d;

        var pressureBarAbsolute =
            pressureBarGauge
            + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

        var nitrogenDensity = LegacyIdealGasDensity(
            temperatureC: temperatureC,
            pressureBarAbsolute: pressureBarAbsolute,
            molarMass: 28.0134d);

        var oxygenDensity = LegacyIdealGasDensity(
            temperatureC: temperatureC,
            pressureBarAbsolute: pressureBarAbsolute,
            molarMass: 31.998d);

        var expectedBaseDensity = LegacyMixtureDensity(
            [
                new LegacyPureDensity(nitrogenDensity, nitrogenPercent),
                new LegacyPureDensity(oxygenDensity, oxygenPercent)
            ]);

        var expectedFinalDensity = expectedBaseDensity + deltaD;

        var actualDensity = CalculateDensity(
            temperatureC: temperatureC,
            pressureBarGauge: pressureBarGauge,
            deltaD: deltaD,
            components:
            [
                new LegacyComponent("N", nitrogenPercent),
                new LegacyComponent("O2", oxygenPercent)
            ]);

        Assert.Equal(expectedFinalDensity, actualDensity, precision: 10);
    }

    /// <summary>
    /// Проверяет жидкостную бинарную смесь.
    ///
    /// Здесь Pressure практически не влияет на выбранные компоненты,
    /// зато отдельно проверяется исходная формула смешения TechDotNetLib.Mix.
    ///
    /// Для ACN используется исходная линейная формула.
    /// Для второго компонента также выбираем ACN-подобную независимую
    /// формулу не следует; поэтому этот тест оставляем для чистого ACN,
    /// а реальную Water + ACN пару будем проверять следующим этапом
    /// непосредственно по контрольным значениям старой программы.
    /// </summary>
    [Fact]
    public void DeltaDIsAddedInEngineeringUnitsBeforeScadaScaling()
    {
        const double temperatureC = 20d;
        const double deltaD = 2.5d;

        var expectedBaseDensity = LegacyAcetonitrileLiquidDensity(temperatureC);
        var expectedFinalDensity = expectedBaseDensity + deltaD;

        var actualDensity = CalculateDensity(
            temperatureC: temperatureC,
            pressureBarGauge: 0d,
            deltaD: deltaD,
            components:
            [
                new LegacyComponent("ACN", 100d)
            ]);

        Assert.Equal(expectedFinalDensity, actualDensity, precision: 10);

        // На уровне Calculation Definition результат остаётся в kg/m³.
        // Старое ×10 относится только к записи SCADA ValCalc.
        Assert.Equal(784.486d, actualDensity, precision: 6);
    }

    /// <summary>
    /// Выполняет расчёт через настоящий новый DensityCalculationDefinition.
    ///
    /// pressureBarGauge = null специально означает,
    /// что ProcessInput Pressure вообще отсутствует.
    /// Тогда Definition обязан использовать DefaultValue = 0 bar(g).
    /// </summary>
    private static double CalculateDensity(
        double temperatureC,
        double? pressureBarGauge,
        double deltaD,
        IReadOnlyList<LegacyComponent> components)
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

        var result = definition.Calculate(
            new CalculationParameterSet(values),
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        return GetOutput(result, "density");
    }

    /// <summary>
    /// Исходная формула жидкого Acetonitrile из TechDotNetLib.
    ///
    /// Старый метод принимает float temperature.
    /// Поэтому перед вычислением намеренно выполняем то же преобразование,
    /// чтобы regression-тест повторял старую точность вычислений.
    /// </summary>
    private static double LegacyAcetonitrileLiquidDensity(double temperatureC)
    {
        var temperature = (float)temperatureC;

        const double a0 = 803.07d;
        const double a1 = -1.0542d;

        return a1 * temperature + a0;
    }

    /// <summary>
    /// Исходная формула Density идеального газа,
    /// использовавшаяся в Nitrogen/Oxygen и других legacy-компонентах.
    ///
    /// Важно: старый GetDensity принимает float temperature и pressure.
    /// Поэтому оба входа сначала приводятся к float.
    /// </summary>
    private static double LegacyIdealGasDensity(
        double temperatureC,
        double pressureBarAbsolute,
        double molarMass)
    {
        var temperature = (float)temperatureC;
        var pressure = (float)pressureBarAbsolute;

        return pressure * Math.Pow(10d, 2d)
            / (LegacyGasConstant / molarMass)
            / (temperature + 273.15d);
    }

    /// <summary>
    /// Полностью повторяет исходную формулу TechDotNetLib.Mix.GetDensity:
    ///
    ///     rhoMix = 1 / Sum(w_i / rho_i)
    ///
    /// Здесь w_i передаётся в процентах и переводится в долю 0..1.
    /// </summary>
    private static double LegacyMixtureDensity(IReadOnlyList<LegacyPureDensity> components)
    {
        var denominator = 0d;

        foreach (var component in components)
            denominator += component.MassPercent * 0.01d / component.DensityKgPerM3;

        return 1d / denominator;
    }

    private static double GetOutput(CalculationResult result, string key)
    {
        return result.Outputs.Single(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private sealed record LegacyComponent(string Code, double MassPercent);

    private sealed record LegacyPureDensity(double DensityKgPerM3, double MassPercent);
}