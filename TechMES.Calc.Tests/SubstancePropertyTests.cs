using TechMES.Calc.Capacity;
using TechMES.Calc.Content;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Tests;

public sealed class SubstancePropertyTests
{
    [Fact]
    public void CatalogContainsLegacyAndExtendedCodes()
    {
        Assert.Equal(55, SubstanceCatalog.Items.Count);

        Assert.Equal(SubstancePhase.Liquid, SubstanceCatalog.GetRequired("ACN").Phase);
        Assert.Equal(SubstancePhase.Vapor, SubstanceCatalog.GetRequired("ACNS").Phase);
        Assert.Equal("Ethanol", SubstanceCatalog.GetRequired("Ethanol").Name);

        // DryMatter является новым TechMES-компонентом, восстановленным из старой PLC ICUMSA-корреляции.
        Assert.Equal(SubstancePhase.Liquid, SubstanceCatalog.GetRequired("DryMatter").Phase);
        Assert.Equal("Dry matter", SubstanceCatalog.GetRequired("DryMatter").Name);

        Assert.True(SubstanceCatalog.GetRequired("ACN").Supports(SubstancePropertySupport.SpecificHeatCapacity));
        Assert.True(SubstanceCatalog.GetRequired("DryMatter").Supports(SubstancePropertySupport.SpecificHeatCapacity));
        Assert.False(SubstanceCatalog.GetRequired("Fusel").Supports(SubstancePropertySupport.SpecificHeatCapacity));
        Assert.False(SubstanceCatalog.GetRequired("Methan").Supports(SubstancePropertySupport.SpecificHeatCapacity));
        Assert.Equal(53,SubstanceCatalog.GetSupported(SubstancePropertySupport.SpecificHeatCapacity).Count);
    }

    [Fact]
    public void PureAcetonitrileDensityIsReturnedInEngineeringUnits()
    {
        var density = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [new MixtureComponent("ACN", 100d)],
            temperatureC: 20d,
            pressureBarAbsolute: 1d);

        Assert.Equal(781.986d, density, precision: 6);
    }

    [Fact]
    public void BinaryDensityUsesLegacyIdealVolumeAdditivityWithoutScadaScaling()
    {
        var density = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [
                new MixtureComponent("ACN", 50d),
                new MixtureComponent("Water", 50d)
            ],
            temperatureC: 20d,
            pressureBarAbsolute: 1d);

        Assert.Equal(876.8993740276006d, density, precision: 8);
    }

    [Fact]
    public void ZeroPercentComponentDoesNotParticipateInDensityCalculation()
    {
        var densityWithoutZeroComponent = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [new MixtureComponent("ACN", 100d)],
            temperatureC: 20d,
            pressureBarAbsolute: 1d);

        var densityWithZeroComponent = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [
                new MixtureComponent("ACN", 100d),
                new MixtureComponent("Fusel", 0d)
            ],
            temperatureC: 20d,
            pressureBarAbsolute: 1d);

        Assert.Equal(densityWithoutZeroComponent, densityWithZeroComponent, precision: 12);
    }

    [Fact]
    public void PureAcetonitrileCapacityIsExplicitlyConvertedToJPerKgK()
    {
        var capacity = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
            [new MixtureComponent("ACN", 100d)],
            temperatureC: 20d);

        Assert.Equal(2221.05154452d, capacity, precision: 8);
    }

    [Fact]
    public void MixturePercentagesMustTotalOneHundred()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateDensityKgPerM3(
                [new MixtureComponent("Water", 80d)],
                temperatureC: 20d,
                pressureBarAbsolute: 1d));

        Assert.Equal("mixture.percent-total-invalid", exception.Code);
    }

    [Fact]
    public void LiquidAndVaporComponentsCannotBeMixed()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateDensityKgPerM3(
                [
                    new MixtureComponent("Water", 50d),
                new MixtureComponent("WaterS", 50d)
                ],
                temperatureC: 20d,
                pressureBarAbsolute: 1.01325d));

        Assert.Equal("mixture.phase-mixed", exception.Code);
    }

    [Fact]
    public void AbsolutePressureMustBeGreaterThanZero()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateDensityKgPerM3(
                [new MixtureComponent("Water", 100d)],
                temperatureC: 20d,
                pressureBarAbsolute: 0d));

        Assert.Equal("mixture.pressure.invalid", exception.Code);
    }

    [Theory]
    [InlineData("Freezium", 20d, 1d)]
    [InlineData("Methan", 298.15d, 3050000d)]
    [InlineData("Fusel", 293.15d, 101325d)]
    [InlineData("HCL", 20d, 1d)]
    [InlineData("HCLS", 20d, 1d)]
    [InlineData("NaOH", 20d, 1d)]
    [InlineData("NaOHS", 20d, 1d)]
    public void LegacyDensityModelsAreExecutedWithoutArtificialBlockList(string code, double temperature, double pressure)
    {
        var density = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [new MixtureComponent(code, 100d)],
            temperatureC: temperature,
            pressureBarAbsolute: pressure);

        Assert.True(double.IsFinite(density));
        Assert.True(density > 0d);
    }

    [Fact]
    public void FreeziumKeepsOriginalTechDotNetDensityScale()
    {
        var density = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [new MixtureComponent("Freezium", 100d)],
            temperatureC: 20d,
            pressureBarAbsolute: 1d);

        // Формула намеренно проверяется в том масштабе,
        // в котором она находилась в оригинальном TechDotNetLib.
        // Здесь ничего не "нормализуем" и не исправляем скрыто.
        Assert.Equal(0.835804514d, density, precision: 9);
    }

    [Theory]
    [InlineData("Diesel", 20d)]
    [InlineData("HCL", 20d)]
    [InlineData("HCLS", 20d)]
    [InlineData("NaOH", 20d)]
    [InlineData("NaOHS", 20d)]
    public void SupportedLegacyCapacityModelsAreExecuted(string code, double temperature)
    {
        var capacity = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
            [new MixtureComponent(code, 100d)],
            temperatureC: temperature);

        Assert.True(double.IsFinite(capacity));
        Assert.True(capacity > 0d);
    }

    [Theory]
    [InlineData("Methan")]
    [InlineData("Fusel")]
    public void UnsupportedCapacityModelsAreRejectedByNormalizedContract(string code)
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [new MixtureComponent(code, 100d)],
                temperatureC: 20d));

        Assert.Equal("substance.capacity.unsupported", exception.Code);
    }

    [Fact]
    public void ZeroPercentUnsupportedCapacityComponentDoesNotParticipateInCalculation()
    {
        var capacityWithoutInactiveComponent =
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [new MixtureComponent("ACN", 100d)],
                temperatureC: 20d);

        var capacityWithInactiveComponent =
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [
                    new MixtureComponent("ACN", 100d),
                    new MixtureComponent("Fusel", 0d)
                ],
                temperatureC: 20d);

        Assert.Equal(
            capacityWithoutInactiveComponent,
            capacityWithInactiveComponent,
            precision: 12);
    }

    [Fact]
    public void ContentFacadeRemovesLegacyHundredthsOfPercentScaling()
    {
        var values = ContentPropertyCalculator.CalculatePercent(
            new ContentCalculationRequest(
                Components: ["PO", "Water"],
                TemperatureC: 50d,
                PressureBarAbsolute: 1d,
                ConfigurationCode: 10));

        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.InRange(value, 0d, 100d));
        Assert.Equal(100d, values.Sum(), precision: 8);
    }

    [Fact]
    public void EmptyContentComponentIsNotSilentlyRemoved()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            ContentPropertyCalculator.CalculatePercent(
                new ContentCalculationRequest(
                    Components: ["PO", "", "Water"],
                    TemperatureC: 50d,
                    PressureBarAbsolute: 1d,
                    ConfigurationCode: 10)));

        Assert.Equal("content.component.code-empty", exception.Code);
    }

    [Fact]
    public void DuplicateContentComponentIsRejected()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            ContentPropertyCalculator.CalculatePercent(
                new ContentCalculationRequest(
                    Components: ["Water", "Water"],
                    TemperatureC: 50d,
                    PressureBarAbsolute: 1d,
                    ConfigurationCode: 10)));

        Assert.Equal("content.component.duplicate", exception.Code);
    }

    [Fact]
    public void UnsupportedContentCombinationFailsExplicitly()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            ContentPropertyCalculator.CalculatePercent(
                new ContentCalculationRequest(
                    Components: ["Water", "P"],
                    TemperatureC: 50d,
                    PressureBarAbsolute: 1d,
                    ConfigurationCode: 10)));

        Assert.Equal("content.components.unsupported", exception.Code);
    }

    /// <summary>
    /// Проверяет Water + DryMatter непосредственно против исходной
    /// PLC-корреляции сахарного раствора.
    ///
    /// Expected вычисляется независимо от TechLib.VSS и DryMatter,
    /// поэтому этот тест действительно контролирует перенос формулы,
    /// а не сравнивает production-код с самим собой.
    /// </summary>
    [Theory]
    [InlineData(0d, 3d)]
    [InlineData(20d, 45d)]
    [InlineData(37.2d, 55d)]
    [InlineData(80d, 70d)]
    [InlineData(120d, 90d)]
    [InlineData(200d, 97d)]
    public void WaterDryMatterDensityMatchesOriginalPlcCorrelation(double temperatureC, double dryMatterPercent)
    {
        var waterPercent = 100d - dryMatterPercent;

        var actualDensity = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [
                new MixtureComponent("Water", waterPercent),
            new MixtureComponent("DryMatter", dryMatterPercent)
            ],
            temperatureC: temperatureC,
            pressureBarAbsolute: 1d);

        var expectedDensity = CalculateOriginalSugarSolutionDensity(temperatureC, dryMatterPercent);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    [Fact]
    public void PureDryMatterRepresentsOneHundredPercentConcentration()
    {
        const double temperatureC = 20d;

        var actualDensity = MixturePropertyCalculator.CalculateDensityKgPerM3(
            [
                new MixtureComponent("DryMatter", 100d)
            ],
            temperatureC: temperatureC,
            pressureBarAbsolute: 1d);

        var expectedDensity = CalculateOriginalSugarSolutionDensity(
            temperatureC: temperatureC,
            dryMatterPercent: 100d);

        Assert.Equal(expectedDensity, actualDensity, precision: 10);
    }

    [Fact]
    public void DryMatterCannotBeMixedWithNonWaterComponent()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateDensityKgPerM3(
                [
                    new MixtureComponent("ACN", 50d),
                new MixtureComponent("DryMatter", 50d)
                ],
                temperatureC: 20d,
                pressureBarAbsolute: 1d));

        Assert.Equal("mixture.drymatter.unsupported-combination", exception.Code);
    }

    [Fact]
    public void WaterDryMatterCapacityMatchesOriginalSugarSolutionFormula()
    {
        const double temperatureC = 84.3;
        const double dryMatterPercent = 55.0;
        const double purityPercent = 90.0;

        var actualCapacity = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
            [
                new MixtureComponent("DryMatter", dryMatterPercent),
                new MixtureComponent("Water", 100.0 - dryMatterPercent)
            ],
                temperatureC: temperatureC,
                additionalParameters: new Dictionary<string, double>{["dryMatterPurityPercent"] = purityPercent});

        var expectedCapacity = CalculateOriginalSugarSolutionCapacity(temperatureC, dryMatterPercent, purityPercent);

        Assert.Equal(expectedCapacity, actualCapacity, precision: 10);
    }

    [Fact]
    public void PureDryMatterCapacityUsesDefaultNinetyPercentPurity()
    {
        const double temperatureC = 20d;

        var actualCapacity = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
            [new MixtureComponent("DryMatter", 100d)],
            temperatureC: temperatureC);

        var expectedCapacity = CalculateOriginalSugarSolutionCapacity(
            temperatureC,
            dryMatterPercent: 100d,
            purityPercent: 90d);

        Assert.Equal(expectedCapacity, actualCapacity, precision: 10);
    }

    [Fact]
    public void CapacityExposesDryMatterPurityAsConfiguration()
    {
        var definition = new CapacityCalculationDefinition();

        var purity = definition.Parameters.Single(parameter => string.Equals(parameter.Key, "dryMatterPurityPercent", StringComparison.OrdinalIgnoreCase));
        var additionalParameter = definition.Parameters.Single(parameter => string.Equals(parameter.Key, "additionalParameter1", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Purity", purity.Name);
        Assert.Equal("%", purity.Unit);
        Assert.Equal(90d, Convert.ToDouble(purity.DefaultValue));
        Assert.Equal(CalculationParameterRole.Configuration, purity.Role);
        Assert.Equal("DryMatter", purity.AppliesToSubstanceCode);

        Assert.Equal("Additional parameter", additionalParameter.Name);
        Assert.Equal(CalculationParameterRole.ProcessInput, additionalParameter.Role);
        Assert.Null(additionalParameter.AppliesToSubstanceCode);

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 84.3d,
                ["dryMatterPurityPercent"] = 90d,
                ["componentCount"] = 2,
                ["component0Code"] = "DryMatter",
                ["component0Percent"] = 55d,
                ["component1Code"] = "Water",
                ["component1Percent"] = 45d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var capacity = result.Outputs.Single(item => string.Equals(item.Key, "capacity", StringComparison.OrdinalIgnoreCase)).Value;

        Assert.Equal(3142.4489964405648d, capacity, precision: 8);
    }

    /// <summary>
    /// Независимое эталонное воспроизведение формулы с PLC.
    ///
    /// Здесь намеренно не вызываются:
    /// TechLib.VW,
    /// TechLib.VSS,
    /// Water,
    /// DryMatter,
    /// MixturePropertyCalculator.
    ///
    /// Если production-формула будет случайно изменена,
    /// этот expected останется прежним и regression-тест упадёт.
    /// </summary>
    private static double CalculateOriginalSugarSolutionDensity(double temperatureC, double dryMatterPercent)
    {
        // Production-компоненты получают Temperature как float,
        // поэтому эталон повторяет тот же входной контракт.
        var t = Math.Max(0d, (float)temperatureC);
        var t2 = t * t;
        var s = dryMatterPercent * 0.01d;

        // Плотность воды из исходного PLC-кода.
        var waterDensity =
            1000.1353
            + 0.00076933504 * t
            - 0.0056218464 * t2
            + 0.000017341396 * t2 * t
            - 0.00000003089613 * t2 * t2;

        // Вклад сухих веществ из исходного PLC-кода.
        var dryMatterContribution =
            s * (385.1761 - 0.1343 * t - 0.0031 * t2)
            + s * s * (154.316 - 0.4357 * t + 0.0016 * t2)
            + s * s * s * (71.52 + 0.842 * t - 0.0055 * t2);

        return waterDensity + dryMatterContribution;
    }

    /// <summary>
    /// Независимое эталонное воспроизведение исходной PLC-формулы CSS.
    ///
    /// Здесь намеренно не вызываются:
    /// TechLib.CSS,
    /// Water,
    /// DryMatter,
    /// MixturePropertyCalculator.
    ///
    /// Temperature сначала приводится к float, потому что исходный
    /// контракт компонентов TechDotNetLib использует GetCapacity(float).
    ///
    /// Благодаря этому тест проверяет именно старую расчётную цепочку,
    /// а не математическую double-версию формулы.
    /// </summary>
    private static double CalculateOriginalSugarSolutionCapacity(double temperatureC, double dryMatterPercent, double purityPercent)
    {
        var t = (double)(float)temperatureC;

        if (t > 0.0)
            return 4218.0 + 2.8 * t * Math.Log10(0.01 * t) - dryMatterPercent * (29.73 - 0.07536 * t - 0.046 * purityPercent);

        return 4186.8 * (1.0 - (0.6 - 0.0018 * t) * dryMatterPercent * 0.01);
    }

    [Theory]
    [InlineData(60d, 0.05d, 10)]   // Ниже минимального давления.
    [InlineData(75d, 0.83d, 10)]   // Интерполяция.
    [InlineData(95d, 1.37d, 10)]   // Интерполяция.
    [InlineData(110d, 2.30d, 10)]  // Выше максимального low-pressure диапазона.
    [InlineData(120d, 2.80d, 20)]  // Ниже high-pressure диапазона.
    [InlineData(130d, 3.57d, 20)]  // Интерполяция high-pressure.
    [InlineData(150d, 5.30d, 20)]  // Выше high-pressure диапазона.
    public void AcnWaterContentMatchesOriginalContentCalc(double temperatureC, double pressureBarAbsolute, int configurationCode)
    {
        var legacy = ContentCalc.ACN_Water_Content((float)temperatureC, (float)pressureBarAbsolute, configurationCode);
        var actual = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
            Components: ["ACN", "Water"],
            TemperatureC: temperatureC,
            PressureBarAbsolute: pressureBarAbsolute,
            ConfigurationCode: configurationCode));

        Assert.Equal(legacy[0] / 100.0, actual[0], precision: 10);
        Assert.Equal(legacy[1] / 100.0, actual[1], precision: 10);
    }

    [Theory]
    [InlineData(60d, 0.8d, 10)]
    [InlineData(100d, 1.5d, 10)]
    [InlineData(120d, 3.5d, 20)]
    public void WaterAcnUsesSameCorrelationWithReversedOutputOrder(double temperatureC, double pressureBarAbsolute, int configurationCode)
    {
        var legacy = ContentCalc.Water_ACN_Content((float)temperatureC, (float)pressureBarAbsolute, configurationCode);
        var actual = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
            Components: ["Water", "ACN"],
            TemperatureC: temperatureC,
            PressureBarAbsolute: pressureBarAbsolute,
            ConfigurationCode: configurationCode));

        Assert.Equal(legacy[0] / 100.0, actual[0], precision: 10);
        Assert.Equal(legacy[1] / 100.0, actual[1], precision: 10);
        Assert.Equal(100.0, actual.Sum(), precision: 10);
    }

    [Theory]

    // ------------------------------------------------------------
    // PO + Propylene
    // ------------------------------------------------------------

    // Ниже минимального давления.
    [InlineData("PO", "P", 60d, 0.8d, 10)]
    // Интерполяция между 1.3 и 1.6.
    [InlineData("PO", "P", 80d, 1.45d, 10)]
    // Тот же расчёт, но обратный порядок output.
    [InlineData("P", "PO", 80d, 1.45d, 10)]
    // Выше максимального давления + legacy no-clamp mode.
    [InlineData("PO", "P", 100d, 2.8d, 11)]

    // ------------------------------------------------------------
    // PO + Water
    // ------------------------------------------------------------

    // Ниже минимального давления.
    [InlineData("PO", "Water", 50d, 0.4d, 10)]
    // Интерполяция.
    [InlineData("PO", "Water", 70d, 1.25d, 10)]
    // Обратный порядок.
    [InlineData("Water", "PO", 70d, 1.25d, 10)]
    // Выше максимального.
    [InlineData("PO", "Water", 90d, 2.2d, 11)]

    // ------------------------------------------------------------
    // ACA + PO
    // ------------------------------------------------------------

    // Ниже минимального давления.
    [InlineData("ACA", "PO", 30d, 0.25d, 10)]
    // Интерполяция.
    [InlineData("ACA", "PO", 50d, 0.725d, 10)]
    // Обратный порядок.
    [InlineData("PO", "ACA", 50d, 0.725d, 10)]
    // Выше максимального.
    [InlineData("ACA", "PO", 80d, 2.2d, 11)]

    // ------------------------------------------------------------
    // ALC + Water
    // ------------------------------------------------------------

    [InlineData("ALC", "Water", 80d, 1.0d, 10)]
    [InlineData("ALC", "Water", 90d, 1.2d, 11)]

    public void RemainingBinaryContentMatchesOriginalContentCalc(string component0, string component1, double temperatureC, double pressureBarAbsolute, int configurationCode)
    {
        var legacy = GetLegacyBinaryContent(component0, component1, (float)temperatureC, (float)pressureBarAbsolute, configurationCode);

        var actual = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
                Components:
                [
                    component0,
                    component1
                ],
                TemperatureC: temperatureC,
                PressureBarAbsolute: pressureBarAbsolute,
                ConfigurationCode: configurationCode));

        Assert.Equal(legacy[0] / 100.0, actual[0], precision: 10);
        Assert.Equal(legacy[1] / 100.0, actual[1], precision: 10);
    }

    [Theory]
    [InlineData("ACN", "Water", "PO", 5d, 0.3d, 10)]       // ниже min pressure + temperature clamp
    [InlineData("ACN", "Water", "PO", 50d, 0.7d, 11)]      // interpolation, clamp disabled
    [InlineData("ACN", "Water", "PO", 60d, 1.0d, 20)]      // exact anchor, 80%
    [InlineData("ACN", "Water", "PO", 100d, 3.2d, 21)]     // above max, clamp disabled
    [InlineData("ACN", "Water", "PO", 80d, 2.3d, 30)]      // 89%, interpolation
    [InlineData("ACN", "Water", "PO", 20d, 1.55d, 40)]     // default group + temperature clamp
    [InlineData("PO", "Water", "ACN", 60d, 1.0d, 20)]      // reverse output order
    [InlineData("PO", "Water", "ACN", 80d, 2.3d, 31)]      // reverse order + no clamp
    public void AcnWaterPoContentMatchesOriginalContentCalc(string component0, string component1, string component2, double temperatureC, double pressureBarAbsolute, int configurationCode)
    {
        var legacy = GetLegacyTernaryContent(component0, component1, component2, (float)temperatureC, (float)pressureBarAbsolute, configurationCode);

        var actual = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
            Components: [component0, component1, component2], TemperatureC: temperatureC,
            PressureBarAbsolute: pressureBarAbsolute, ConfigurationCode: configurationCode));

        Assert.Equal(legacy[0] / 100.0, actual[0], precision: 10);
        Assert.Equal(legacy[1] / 100.0, actual[1], precision: 10);
        Assert.Equal(legacy[2] / 100.0, actual[2], precision: 10);
        Assert.Equal(100.0, actual.Sum(), precision: 10);
    }

    [Fact]
    public void AcnWaterPoAboveMaximumPressureWithTemperatureClampReturnsCalculationError()
    {
        var exception = Assert.Throws<CalculationException>(() => ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(Components: ["ACN", "Water", "PO"], TemperatureC: 80d, PressureBarAbsolute: 3.2d, ConfigurationCode: 10)));

        Assert.Equal("content.pressure.out-of-range", exception.Code);
    }

    private static double[] GetLegacyTernaryContent(string component0, string component1, string component2, float temperatureC, float pressureBarAbsolute, int configurationCode)
    {
        return (component0, component1, component2) switch
        {
            ("ACN", "Water", "PO") => ContentCalc.ACN_Water_PO_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("PO", "Water", "ACN") => ContentCalc.PO_Water_ACN_Content(temperatureC, pressureBarAbsolute, configurationCode),
            _ => throw new InvalidOperationException($"Legacy ternary Content combination '{component0} + {component1} + {component2}' is not defined.")
        };
    }

    /// <summary>
    /// Вызывает именно исходную legacy-функцию ContentCalc.
    ///
    /// Этот helper нужен только regression-тестам.
    /// Production-код через ContentCalc для бинарных систем после Content 2 больше не выполняется.
    /// </summary>
    private static double[] GetLegacyBinaryContent(string component0, string component1, float temperatureC, float pressureBarAbsolute, int configurationCode)
    {
        return (component0, component1) switch
        {
            ("PO", "P") => ContentCalc.PO_P_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("P", "PO") => ContentCalc.P_PO_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("PO", "Water") => ContentCalc.PO_Water_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("Water", "PO") => ContentCalc.Water_PO_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("ACA", "PO") => ContentCalc.ACA_PO_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("PO", "ACA") => ContentCalc.PO_ACA_Content(temperatureC, pressureBarAbsolute, configurationCode),
            ("ALC", "Water") => ContentCalc.ALC_Water_Content(temperatureC, pressureBarAbsolute, configurationCode),

            _ => throw new InvalidOperationException($"Legacy binary Content combination '{component0} + {component1}' is not defined.")
        };
    }
}
