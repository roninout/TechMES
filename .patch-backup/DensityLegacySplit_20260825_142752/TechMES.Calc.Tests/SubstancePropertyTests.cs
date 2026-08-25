using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;
using TechMES.Calc.Substances.Content;

namespace TechMES.Calc.Tests;

public sealed class SubstancePropertyTests
{
    [Fact]
    public void CatalogContainsAllLegacyCodes()
    {
        Assert.Equal(54, SubstanceCatalog.Items.Count);
        Assert.Equal(SubstancePhase.Liquid, SubstanceCatalog.GetRequired("ACN").Phase);
        Assert.Equal(SubstancePhase.Vapor, SubstanceCatalog.GetRequired("ACNS").Phase);
        Assert.Equal("Ethanol", SubstanceCatalog.GetRequired("Ethanol").Name);
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
    [InlineData("Freezium")]
    [InlineData("Methan")]
    [InlineData("Fusel")]
    [InlineData("HCL")]
    [InlineData("HCLS")]
    [InlineData("NaOH")]
    [InlineData("NaOHS")]
    public void DensityDoesNotUseUnverifiedLegacyModels(string code)
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateDensityKgPerM3(
                [new MixtureComponent(code, 100d)],
                temperatureC: 20d,
                pressureBarAbsolute: 1d));

        Assert.Equal("substance.density.units-not-normalized", exception.Code);
    }

    [Theory]
    [InlineData("Methan")]
    [InlineData("Fusel")]
    [InlineData("Diesel")]
    [InlineData("HCL")]
    [InlineData("HCLS")]
    [InlineData("NaOH")]
    [InlineData("NaOHS")]
    public void CapacityDoesNotUseUnverifiedLegacyModels(string code)
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [new MixtureComponent(code, 100d)],
                temperatureC: 20d));

        Assert.Equal("substance.capacity.units-not-normalized", exception.Code);
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
}