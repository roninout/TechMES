using TechMES.Calc.Content;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Mixtures;
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

        Assert.True(
            SubstanceCatalog.GetRequired("ACN")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.False(
            SubstanceCatalog.GetRequired("DryMatter")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.False(
            SubstanceCatalog.GetRequired("Fusel")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.False(
            SubstanceCatalog.GetRequired("Methan")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.Equal(
            52,
            SubstanceCatalog.GetSupported(SubstancePropertySupport.SpecificHeatCapacity).Count);
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
    [InlineData("DryMatter")]
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
}
