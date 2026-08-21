using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Tanks.Types;

namespace TechMES.Calc.Tests;

/// <summary>
/// Regression tests общей Tank infrastructure и полностью
/// переведённого на новую модель Tank Type 1.
///
/// Type 2..8 будут получать собственные geometry tests
/// по мере их перевода на новую реализацию.
/// </summary>
public sealed class TankTypeVolumeDefinitionTests
{
    [Fact]
    public void BuiltInCatalogContainsAllEightTankTypes()
    {
        var catalog = BuiltInCalculationCatalog.Create();

        for (var type = 1; type <= 8; type++)
        {
            var definition = catalog.GetRequired($"tank.volume.type{type}");

            Assert.Equal("Tanks", definition.Category);
            Assert.Equal("3", definition.Version);
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

    #region TYPE 1
    // ============================================================
    // TYPE 1
    // ============================================================

    [Fact]
    public void Type1CalculatesKnownWorkingPoint()
    {
        // Geometry:
        // dimA = 2500 mm
        // dimB = 1600 mm
        // dimC = 400 mm
        //
        // Total physical height:
        // 2500 + 400 + 400 = 3300 mm
        //
        // Sensor:
        // lower dead = 150 mm
        // upper dead = 150 mm
        // measurement area = 3000 mm
        //
        // Level.R = 49.8 %
        // measured level = 1494 mm
        // physical liquid height = 150 + 1494 = 1644 mm

        var result = CalculateType1(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            3000.0,
            GetOutput(result, "hMax"),
            precision: 10);

        Assert.Equal(
            1494.0,
            GetOutput(result, "levelMm"),
            precision: 10);

        Assert.Equal(
            3.0373755532947078,
            GetOutput(result, "volume"),
            precision: 10);

        Assert.Equal(
            3.699827161468283,
            GetOutput(result, "mass"),
            precision: 10);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(200.0, 0.1675516081914557)]
    [InlineData(400.0, 0.5361651462126582)]
    [InlineData(1650.0, 3.049439269084493)]
    [InlineData(2900.0, 5.562713391956327)]
    [InlineData(3100.0, 5.931326929977530)]
    [InlineData(3300.0, 6.098878538168986)]
    public void Type1VolumeMatchesReferenceGeometry(double liquidHeightMm, double expectedVolumeM3)
    {
        const double totalHeightMm = 3300.0;

        // При нулевых dead areas Level.R напрямую задаёт
        // физическую высоту жидкости относительно полной высоты Tank.
        var levelRaw = liquidHeightMm / totalHeightMm * 100.0;

        var result = CalculateType1(
            levelRaw: levelRaw,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            liquidHeightMm,
            GetOutput(result, "levelMm"),
            precision: 8);

        Assert.Equal(
            expectedVolumeM3,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void Type1StopsVolumeAtMeasurementBoundaryWhenAbove100IsDisabled()
    {
        var result = CalculateType1(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Level остаётся реальным и не ограничивается 100 %.
        Assert.Equal(
            3300.0,
            GetOutput(result, "levelMm"),
            precision: 10);

        // Но Volume останавливается на 3150 mm:
        // 150 lower dead + 3000 measurement area.
        Assert.Equal(
            5.999918369580907,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void Type1ContinuesVolumeIntoUpperDeadAreaWhenAbove100IsEnabled()
    {
        var result = CalculateType1(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // 110 % выводит расчёт выше рабочей зоны,
        // но физическая высота ограничивается полной геометрией Tank.
        Assert.Equal(
            3300.0,
            GetOutput(result, "levelMm"),
            precision: 10);

        Assert.Equal(
            6.098878538168986,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void NegativeLevelProducesZeroMeasuredLevel()
    {
        var result = CalculateType1(
            levelRaw: -25.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            0.0,
            GetOutput(result, "levelMm"),
            precision: 10);
    }

    [Fact]
    public void NonPositiveDensityProducesZeroMass()
    {
        var result = CalculateType1(
            levelRaw: 50.0,
            densityHmi: 0.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            0.0,
            GetOutput(result, "mass"),
            precision: 10);
    }

    [Fact]
    public void Type1RejectsZeroDiameter()
    {
        var result = CalculateType1(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 0,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            "dimB",
            result.ErrorMessage ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsInvalidMeasurementArea()
    {
        var result = CalculateType1(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 1700,
            lowerDeadArea: 1600,
            calculateAbove100: false);

        Assert.False(result.IsSuccess);

        Assert.Contains(
            "measurement area",
            result.ErrorMessage ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Type1ReturnsCalculationHelpTrace()
    {
        var result = CalculateType1(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false,
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.sensor.measurement.formula");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.geometry.radius.formula");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.volume.head.formula");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.volume.current.region");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.result.volume.calculation");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.result.mass.calculation");
    }
    #endregion

    #region TYPE 2
    // ============================================================
    // TYPE 2
    // ============================================================

    [Fact]
    public void Type2CalculatesKnownWorkingPoint()
    {
        // Geometry:
        //
        // dimA = 2500 mm - cylindrical length
        // dimB = 1600 mm - diameter
        // dimC = 400 mm  - one elliptical end depth
        //
        // Sensor:
        //
        // lower dead = 150 mm
        // upper dead = 150 mm
        // measurement area = 1300 mm
        //
        // Level.R = 49.8 %
        // measured level = 647.4 mm
        // physical liquid height = 797.4 mm

        var result = CalculateType2(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            1300.0,
            GetOutput(result, "hMax"),
            precision: 10);

        Assert.Equal(
            647.4,
            GetOutput(result, "levelMm"),
            precision: 10);

        Assert.Equal(
            3.0364254915078415,
            GetOutput(result, "volume"),
            precision: 10);

        Assert.Equal(
            3.698669891205701,
            GetOutput(result, "mass"),
            precision: 10);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(200.0, 0.408726095434738)]
    [InlineData(400.0, 1.150247367078461)]
    [InlineData(800.0, 3.049439269084493)]
    [InlineData(1200.0, 4.948631171090524)]
    [InlineData(1400.0, 5.690152442734247)]
    [InlineData(1600.0, 6.098878538168986)]
    public void Type2VolumeMatchesReferenceGeometry(double liquidHeightMm, double expectedVolumeM3)
    {
        const double diameterMm = 1600.0;

        // При нулевых dead areas Level.R напрямую задаёт
        // физическую высоту жидкости относительно диаметра Tank.
        var levelRaw = liquidHeightMm / diameterMm * 100.0;

        var result = CalculateType2(
            levelRaw: levelRaw,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            liquidHeightMm,
            GetOutput(result, "levelMm"),
            precision: 8);

        Assert.Equal(
            expectedVolumeM3,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void Type2IsExactlyHalfFullAtTankCenterline()
    {
        var result = CalculateType2(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var halfVolume = GetOutput(result, "volume");

        // Полный объём этой геометрии = 6.098878538168986 m³.
        Assert.Equal(
            3.049439269084493,
            halfVolume,
            precision: 10);
    }

    [Fact]
    public void Type2StopsVolumeAtMeasurementBoundaryWhenAbove100IsDisabled()
    {
        var result = CalculateType2(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Level не ограничивается 100 %:
        //
        // 1300 × 110 % = 1430 mm.
        Assert.Equal(
            1430.0,
            GetOutput(result, "levelMm"),
            precision: 10);

        // Volume останавливается на верхней границе
        // рабочей зоны:
        //
        // 150 + 1300 = 1450 mm.
        Assert.Equal(
            5.834431316529754,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void Type2ContinuesVolumeIntoUpperDeadAreaWhenAbove100IsEnabled()
    {
        var result = CalculateType2(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // 1300 × 110 % = 1430 mm.
        //
        // Physical height:
        // 150 + 1430 = 1580 mm.
        Assert.Equal(
            1430.0,
            GetOutput(result, "levelMm"),
            precision: 10);

        Assert.Equal(
            6.086499197927015,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void Type2RejectsZeroDiameter()
    {
        var result = CalculateType2(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 0,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: false);

        Assert.False(result.IsSuccess);

        Assert.Contains(
            "dimB",
            result.ErrorMessage ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Type2ReturnsCalculationHelpTrace()
    {
        var result = CalculateType2(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false,
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.geometry.radius.formula");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.volume.segment.formula");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.volume.head.formula");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.volume.heads.calculation");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.result.volume.calculation");

        Assert.Contains(
            result.Trace,
            item => item.Key == "help.result.mass.calculation");
    }
    #endregion

    #region TYPE 3
    // ============================================================
    // TYPE 3
    // ============================================================

    [Fact]
    public void Type3CalculatesKnownWorkingPoint()
    {
        // Geometry:
        //
        // dimA = 2500 mm - cylindrical height
        // dimB = 1600 mm - Tank diameter
        // dimC = 400 mm  - lower elliptical head height
        // dimD = 350 mm  - partition distance from left wall
        //
        // Total height:
        // 2500 + 400 = 2900 mm
        //
        // Sensor:
        // upper dead = 150 mm
        // lower dead = 150 mm
        // measurement area = 2600 mm
        //
        // Level.R = 49.8 %
        // measured level = 1294.8 mm
        // physical liquid height = 150 + 1294.8 = 1444.8 mm

        var result = CalculateType3(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 350,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(2600.0, GetOutput(result, "hMax"), precision: 10);
        Assert.Equal(1294.8, GetOutput(result, "levelMm"), precision: 10);
        Assert.Equal(0.40564134015804704, GetOutput(result, "volume"), precision: 9);
        Assert.Equal(0.4941117164465171, GetOutput(result, "mass"), precision: 9);
    }

    [Fact]
    public void Type3CenteredPartitionProducesExactlyHalfOfFullTank()
    {
        var result = CalculateType3(
            levelRaw: 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 800,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Полный Tank с одним нижним эллиптическим днищем:
        //
        // Vtank = πR²A + 2/3πR²C
        //       = 5.562713391956327 m³
        //
        // Перегородка проходит точно через центр,
        // поэтому левый отсек равен половине Tank.
        Assert.Equal(2.7813566959781637, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type3PartitionAtRightWallProducesFullTankVolume()
    {
        // dimD = dimB означает, что перегородка совпадает
        // с правой стенкой Tank.
        //
        // Следовательно рассчитываемый левый отсек
        // становится всем Tank целиком.
        //
        // Geometry:
        // dimA = 2500 mm
        // dimB = 1600 mm
        // dimC = 400 mm
        //
        // Vbody = π × R² × dimA
        // Vhead = 2/3 × π × R² × dimC
        //
        // Vfull = 5.562713391956327 m³.

        var result = CalculateType3(
            levelRaw: 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1600,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(
            5.562713391956327,
            GetOutput(result, "volume"),
            precision: 10);
    }

    [Fact]
    public void Type3PartialLowerHeadAtCenteredPartitionIsExactlyHalfOfHeadVolume()
    {
        const double totalHeightMm = 2900.0;
        const double liquidHeightMm = 200.0;

        var result = CalculateType3(
            levelRaw: liquidHeightMm / totalHeightMm * 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 800,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // При центральной перегородке каждое горизонтальное
        // сечение нижнего днища делится ровно пополам.
        //
        // Полный объём нижнего эллиптического сегмента до 200 mm:
        // 0.1675516081914557 m³
        //
        // Левая половина:
        // 0.08377580409572785 m³
        Assert.Equal(0.08377580409572785, GetOutput(result, "volume"), precision: 8);
    }

    [Fact]
    public void Type3MatchesReferenceVolumeAtFullOffCenterCompartment()
    {
        var result = CalculateType3(
            levelRaw: 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 350,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Точная площадь цилиндрического отсека:
        // 0.3253225200009434 m²
        //
        // Точный объём части нижнего полуэллипсоида:
        // 0.06574437126106138 m³
        //
        // V = 0.06574437126106138
        //   + 0.3253225200009434 × 2.5
        //   = 0.87905067126342 m³
        Assert.Equal(0.87905067126342, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type3StopsVolumeAtMeasurementBoundaryWhenAbove100IsDisabled()
    {
        var result = CalculateType3(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 350,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Measurement area:
        // 2900 - 150 - 150 = 2600 mm
        //
        // LevelMm:
        // 2600 × 110% = 2860 mm
        Assert.Equal(2860.0, GetOutput(result, "levelMm"), precision: 10);

        // Volume ограничивается физической отметкой:
        // 150 + 2600 = 2750 mm.
        Assert.Equal(0.8302522932632784, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type3ContinuesVolumeIntoUpperDeadAreaWhenAbove100IsEnabled()
    {
        var result = CalculateType3(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 350,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(2860.0, GetOutput(result, "levelMm"), precision: 10);

        // Raw physical level:
        // 150 + 2860 = 3010 mm
        //
        // Tank имеет физическую высоту 2900 mm,
        // поэтому расчёт ограничивается полным Tank.
        Assert.Equal(0.87905067126342, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type3RejectsPartitionOutsideTank()
    {
        var result = CalculateType3(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1700,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Type3ReturnsCalculationHelpTrace()
    {
        var result = CalculateType3(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 350,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false,
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(result.Trace, item => item.Key == "help.geometry.partition.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.body-segment.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.head-integral.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.full-head.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.volume.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.mass.calculation");
    }
    #endregion

    #region TYPE 4
    // ============================================================
    // TYPE 4
    // ============================================================

    [Fact]
    public void Type4CalculatesKnownWorkingPoint()
    {
        // Geometry:
        //
        // dimA = 2500 mm - Tank height
        // dimB = 1600 mm - Tank width
        // dimC = 400 mm  - Tank depth
        //
        // Base area:
        //
        // 1.6 × 0.4 = 0.64 m²
        //
        // Sensor:
        //
        // upper dead = 150 mm
        // lower dead = 150 mm
        //
        // Measurement area:
        //
        // 2500 - 150 - 150 = 2200 mm
        //
        // Level.R = 49.8 %
        //
        // Measured level:
        //
        // 2200 × 49.8 / 100 = 1095.6 mm
        //
        // Physical liquid height:
        //
        // 150 + 1095.6 = 1245.6 mm
        //
        // Volume:
        //
        // 0.64 × 1.2456 = 0.797184 m³

        var result = CalculateType4(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(2200.0, GetOutput(result, "hMax"), precision: 10);
        Assert.Equal(1095.6, GetOutput(result, "levelMm"), precision: 10);
        Assert.Equal(0.797184, GetOutput(result, "volume"), precision: 10);
        Assert.Equal(0.9710498304, GetOutput(result, "mass"), precision: 10);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(625.0, 0.4)]
    [InlineData(1250.0, 0.8)]
    [InlineData(1875.0, 1.2)]
    [InlineData(2500.0, 1.6)]
    public void Type4VolumeMatchesReferenceGeometry(double liquidHeightMm, double expectedVolumeM3)
    {
        const double totalHeightMm = 2500.0;

        // При нулевых dead areas Level.R напрямую задаёт
        // физическую высоту жидкости по всей высоте Tank.
        var levelRaw = liquidHeightMm / totalHeightMm * 100.0;

        var result = CalculateType4(
            levelRaw: levelRaw,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(liquidHeightMm, GetOutput(result, "levelMm"), precision: 8);
        Assert.Equal(expectedVolumeM3, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type4StopsVolumeAtMeasurementBoundaryWhenAbove100IsDisabled()
    {
        var result = CalculateType4(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Measurement area:
        //
        // 2500 - 150 - 150 = 2200 mm
        //
        // LevelMm:
        //
        // 2200 × 110% = 2420 mm.
        //
        // Сам Level не ограничиваем значением 100%.
        Assert.Equal(2420.0, GetOutput(result, "levelMm"), precision: 10);

        // Calculate above 100% = OFF.
        //
        // Volume останавливается на верхней границе
        // рабочей зоны датчика:
        //
        // Hvolume = 150 + 2200 = 2350 mm
        //
        // V = 1.6 × 0.4 × 2.35
        //   = 1.504 m³
        Assert.Equal(1.504, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type4ContinuesVolumeIntoUpperDeadAreaWhenAbove100IsEnabled()
    {
        var result = CalculateType4(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Measured Level остаётся:
        //
        // 2200 × 110% = 2420 mm.
        Assert.Equal(2420.0, GetOutput(result, "levelMm"), precision: 10);

        // Raw physical height:
        //
        // 150 + 2420 = 2570 mm.
        //
        // Но физическая высота Tank = 2500 mm,
        // поэтому объём ограничивается полным Tank:
        //
        // Vfull = 1.6 × 0.4 × 2.5 = 1.6 m³.
        Assert.Equal(1.6, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type4RejectsZeroDepth()
    {
        var result = CalculateType4(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 0,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: false);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Type4ReturnsCalculationHelpTrace()
    {
        var result = CalculateType4(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false,
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(result.Trace, item => item.Key == "help.geometry.height.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.base-area.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.base-area.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.full.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.volume.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.mass.calculation");
    }
    #endregion

    #region TYPE 5
    // ============================================================
    // TYPE 5
    // ============================================================

    [Fact]
    public void Type5CalculatesKnownWorkingPoint()
    {
        // Geometry:
        //
        // dimA = 2500 mm - cylindrical height
        // dimB = 1600 mm - Tank diameter
        // dimC = 400 mm  - lower elliptical head
        //
        // dimD = 1000 mm - tube bundle height
        // dimE = 500 mm  - distance from Tank top to bundle
        // dimF = 120 L   - total tube displacement volume
        //
        // Total height:
        //
        // 2500 + 400 = 2900 mm
        //
        // Tube bundle:
        //
        // bottom = 400 + 2500 - 500 - 1000 = 1400 mm
        // top    = 400 + 2500 - 500        = 2400 mm
        //
        // Sensor:
        //
        // upper dead = 150 mm
        // lower dead = 150 mm
        //
        // measurement area = 2600 mm
        //
        // Level.R = 49.8 %
        //
        // measured level:
        //
        // 2600 × 49.8% = 1294.8 mm
        //
        // physical height:
        //
        // 150 + 1294.8 = 1444.8 mm
        //
        // То есть жидкость уже вошла в зону трубного пучка
        // на 44.8 mm.

        var result = CalculateType5(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(2600.0, GetOutput(result, "hMax"), precision: 10);
        Assert.Equal(1294.8, GetOutput(result, "levelMm"), precision: 10);
        Assert.Equal(2.631484189073852, GetOutput(result, "volume"), precision: 10);
        Assert.Equal(3.205410890710859, GetOutput(result, "mass"), precision: 10);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(200.0, 0.1675516081914557)]
    [InlineData(400.0, 0.5361651462126582)]
    [InlineData(1000.0, 1.7425367251911388)]
    [InlineData(1400.0, 2.5467844445101253)]
    [InlineData(1900.0, 3.4920940936588596)]
    [InlineData(2400.0, 4.437403742807594)]
    [InlineData(2900.0, 5.442713391956327)]
    public void Type5VolumeMatchesReferenceGeometry(double liquidHeightMm, double expectedVolumeM3)
    {
        const double totalHeightMm = 2900.0;

        // Dead areas = 0.
        //
        // Поэтому Level.R напрямую переводим
        // в физическую высоту всего Tank.
        var levelRaw = liquidHeightMm / totalHeightMm * 100.0;

        var result = CalculateType5(
            levelRaw: levelRaw,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(liquidHeightMm, GetOutput(result, "levelMm"), precision: 8);
        Assert.Equal(expectedVolumeM3, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type5FullVolumeSubtractsTubeDisplacement()
    {
        var withoutTubes = CalculateType5(
            levelRaw: 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 0,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        var withTubes = CalculateType5(
            levelRaw: 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(withoutTubes.IsSuccess, withoutTubes.ErrorMessage);
        Assert.True(withTubes.IsSuccess, withTubes.ErrorMessage);

        // Полный геометрический Tank:
        //
        // 5.562713391956327 m³.
        //
        // Трубки занимают:
        //
        // 120 L = 0.120 m³.
        //
        // Поэтому полезный объём должен уменьшиться
        // ровно на 0.120 m³.
        Assert.Equal(5.562713391956327, GetOutput(withoutTubes, "volume"), precision: 10);
        Assert.Equal(5.442713391956327, GetOutput(withTubes, "volume"), precision: 10);
        Assert.Equal(0.120, GetOutput(withoutTubes, "volume") - GetOutput(withTubes, "volume"), precision: 10);
    }

    [Fact]
    public void Type5TubeDisplacementGrowsLinearlyInsideBundle()
    {
        // Нижняя граница пучка = 1400 mm.
        // Верхняя граница пучка = 2400 mm.
        //
        // На отметке 1900 mm пучок затоплен ровно на 50%.
        //
        // Значит из геометрического объёма
        // должно вычитаться:
        //
        // 120 L × 50% = 60 L = 0.060 m³.

        var result = CalculateType5(
            levelRaw: 1900.0 / 2900.0 * 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(3.4920940936588596, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type5StopsVolumeAtMeasurementBoundaryWhenAbove100IsDisabled()
    {
        var result = CalculateType5(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Measurement area:
        //
        // 2900 - 150 - 150 = 2600 mm.
        //
        // Measured level:
        //
        // 2600 × 110% = 2860 mm.
        Assert.Equal(2860.0, GetOutput(result, "levelMm"), precision: 10);

        // Calculate above 100% = OFF.
        //
        // Volume останавливается на:
        //
        // 150 + 2600 = 2750 mm.
        Assert.Equal(5.141120497211708, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type5ContinuesVolumeIntoUpperDeadAreaWhenAbove100IsEnabled()
    {
        var result = CalculateType5(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(2860.0, GetOutput(result, "levelMm"), precision: 10);

        // Raw physical height:
        //
        // 150 + 2860 = 3010 mm.
        //
        // Полная физическая высота = 2900 mm,
        // поэтому получаем полный полезный объём Tank.
        Assert.Equal(5.442713391956327, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type5RejectsTubeBundleOutsideCylindricalPart()
    {
        var result = CalculateType5(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 2200,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: false);

        // 2200 + 500 > 2500.
        //
        // Пучок физически не помещается
        // внутри цилиндрической части.
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Type5RejectsTubeVolumeGreaterThanBundleRegion()
    {
        var result = CalculateType5(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 2500,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: false);

        // При D=1600 mm и bundle height=1000 mm
        // геометрический объём области пучка ≈ 2010.6 L.
        //
        // 2500 L труб в неё физически не помещаются.
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Type5ReturnsCalculationHelpTrace()
    {
        var result = CalculateType5(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            dimD: 1000,
            dimE: 500,
            dimF: 120,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false,
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(result.Trace, item => item.Key == "help.geometry.bundle-bottom.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.geometry.tube-volume.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.head.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.tube-displacement.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.full-net.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.volume.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.mass.calculation");
    }

    [Fact]
    public void Type5HasExpectedInitialGeometryDefaults()
    {
        // Type 5 имеет собственные defaults для параметров,
        // смысл которых отличается от других Tank Type.
        //
        // Поэтому эти значения задаются непосредственно
        // в TankType5VolumeDefinition, а не в общей Base.

        var definition = new TankType5VolumeDefinition();

        var parameters = definition.Parameters.ToDictionary(
            parameter => parameter.Key,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2500d, Convert.ToDouble(parameters["dimA"].DefaultValue));
        Assert.Equal(1600d, Convert.ToDouble(parameters["dimB"].DefaultValue));
        Assert.Equal(400d, Convert.ToDouble(parameters["dimC"].DefaultValue));

        Assert.Equal(2000d, Convert.ToDouble(parameters["dimD"].DefaultValue));
        Assert.Equal(200d, Convert.ToDouble(parameters["dimE"].DefaultValue));
        Assert.Equal(100d, Convert.ToDouble(parameters["dimF"].DefaultValue));
    }
    #endregion

    #region TYPE 6
    // ============================================================
    // TYPE 6
    // ============================================================

    [Fact]
    public void Type6CalculatesKnownWorkingPoint()
    {
        // Geometry:
        //
        // dimA = 2500 mm - cylindrical height
        // dimB = 1600 mm - Tank diameter
        // dimC = 400 mm  - height of each conical head
        //
        // Total height:
        //
        // 400 + 2500 + 400 = 3300 mm
        //
        // Sensor:
        //
        // upper dead = 150 mm
        // lower dead = 150 mm
        //
        // Measurement area:
        //
        // 3300 - 150 - 150 = 3000 mm
        //
        // Level.R = 49.8 %
        //
        // Measured level:
        //
        // 3000 × 49.8% = 1494 mm
        //
        // Physical height:
        //
        // 150 + 1494 = 1644 mm
        //
        // Это цилиндрическая часть:
        //
        // hcyl = 1644 - 400 = 1244 mm.

        var result = CalculateType6(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(3000.0, GetOutput(result, "hMax"), precision: 10);
        Assert.Equal(1494.0, GetOutput(result, "levelMm"), precision: 10);
        Assert.Equal(2.7692929801883786, GetOutput(result, "volume"), precision: 10);
        Assert.Equal(3.3732757791674635, GetOutput(result, "mass"), precision: 10);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(200.0, 0.03351032163829113)]
    [InlineData(400.0, 0.26808257310632905)]
    [InlineData(1000.0, 1.4744541520848096)]
    [InlineData(2900.0, 5.294630818849998)]
    [InlineData(3100.0, 5.529203070318037)]
    [InlineData(3300.0, 5.562713391956327)]
    public void Type6VolumeMatchesReferenceGeometry(double liquidHeightMm, double expectedVolumeM3)
    {
        const double totalHeightMm = 3300.0;

        // Dead areas = 0.
        //
        // Поэтому Level.R напрямую задаёт
        // физическую высоту жидкости от нижней вершины Tank.
        var levelRaw = liquidHeightMm / totalHeightMm * 100.0;

        var result = CalculateType6(
            levelRaw: levelRaw,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Equal(liquidHeightMm, GetOutput(result, "levelMm"), precision: 8);
        Assert.Equal(expectedVolumeM3, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type6FullTankUsesExactConeGeometry()
    {
        var result = CalculateType6(
            levelRaw: 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // R = 0.8 m
        //
        // One cone:
        //
        // Vcone = 1/3 × π × 0.8² × 0.4
        //       = 0.26808257310632905 m³
        //
        // Cylinder:
        //
        // Vcyl = π × 0.8² × 2.5
        //      = 5.026548245743669 m³
        //
        // Full Tank:
        //
        // 0.26808257310632905
        // + 5.026548245743669
        // + 0.26808257310632905
        // = 5.562713391956327 m³.

        Assert.Equal(5.562713391956327, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type6LowerConeHalfHeightHasOneEighthOfFullConeVolume()
    {
        const double totalHeightMm = 3300.0;
        const double liquidHeightMm = 200.0;

        var result = CalculateType6(
            levelRaw: liquidHeightMm / totalHeightMm * 100.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Для конуса подобные размеры изменяются линейно.
        //
        // При h = C / 2:
        //
        // V / Vfull = (h / C)³
        //           = (1 / 2)³
        //           = 1 / 8.
        //
        // Full cone = 0.26808257310632905 m³.
        //
        // Half-height volume:
        //
        // 0.26808257310632905 / 8
        // = 0.03351032163829113 m³.

        Assert.Equal(0.03351032163829113, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type6StopsVolumeAtMeasurementBoundaryWhenAbove100IsDisabled()
    {
        var result = CalculateType6(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Level НЕ ограничиваем:
        //
        // 3000 × 110% = 3300 mm.
        Assert.Equal(3300.0, GetOutput(result, "levelMm"), precision: 10);

        // Volume при OFF останавливается на верхней границе
        // Measurement area:
        //
        // 150 + 3000 = 3150 mm.
        //
        // Это 250 mm внутри верхнего конуса.

        Assert.Equal(5.548576225015173, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type6ContinuesVolumeIntoUpperDeadAreaWhenAbove100IsEnabled()
    {
        var result = CalculateType6(
            levelRaw: 110.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Level остаётся 3300 mm.
        Assert.Equal(3300.0, GetOutput(result, "levelMm"), precision: 10);

        // Raw physical height:
        //
        // 150 + 3300 = 3450 mm.
        //
        // Но физическая высота Tank = 3300 mm.
        //
        // Поэтому Volume ограничивается полным Tank.

        Assert.Equal(5.562713391956327, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type6ZeroHeadHeightBecomesPlainCylinder()
    {
        var result = CalculateType6(
            levelRaw: 50.0,
            densityHmi: 1000.0,
            dimA: 2500,
            dimB: 1600,
            dimC: 0,
            upperDeadArea: 0,
            lowerDeadArea: 0,
            calculateAbove100: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // dimC = 0:
        //
        // Tank становится обычным цилиндром.
        //
        // 50% от 2500 mm = 1250 mm.
        //
        // V = π × 0.8² × 1.25
        //   = 2.5132741228718345 m³.

        Assert.Equal(1250.0, GetOutput(result, "levelMm"), precision: 10);
        Assert.Equal(2.5132741228718345, GetOutput(result, "volume"), precision: 10);
    }

    [Fact]
    public void Type6ReturnsCalculationHelpTrace()
    {
        var result = CalculateType6(
            levelRaw: 49.8,
            densityHmi: 1218.1,
            dimA: 2500,
            dimB: 1600,
            dimC: 400,
            upperDeadArea: 150,
            lowerDeadArea: 150,
            calculateAbove100: false,
            includeTrace: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        Assert.Contains(result.Trace, item => item.Key == "help.geometry.total-height.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.head.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.lower.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.volume.upper.formula");
        Assert.Contains(result.Trace, item => item.Key == "help.result.volume.calculation");
        Assert.Contains(result.Trace, item => item.Key == "help.result.mass.calculation");
    }
    #endregion


    private static CalculationResult CalculateType1(double levelRaw, double densityHmi, double dimA, double dimB, double dimC, double upperDeadArea, double lowerDeadArea, bool calculateAbove100, bool includeTrace = false)
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelRaw"] = levelRaw,
                ["densityHmi"] = densityHmi,

                ["dimA"] = dimA,
                ["dimB"] = dimB,
                ["dimC"] = dimC,

                ["upperDeadArea"] = upperDeadArea,
                ["lowerDeadArea"] = lowerDeadArea,
                ["calculateAbove100"] = calculateAbove100
            });

        return new TankType1VolumeDefinition()
            .Calculate(parameters, includeTrace);
    }

    private static CalculationResult CalculateType2(double levelRaw, double densityHmi, double dimA, double dimB, double dimC, double upperDeadArea, double lowerDeadArea, bool calculateAbove100, bool includeTrace = false)
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelRaw"] = levelRaw,
                ["densityHmi"] = densityHmi,

                ["dimA"] = dimA,
                ["dimB"] = dimB,
                ["dimC"] = dimC,

                ["upperDeadArea"] = upperDeadArea,
                ["lowerDeadArea"] = lowerDeadArea,
                ["calculateAbove100"] = calculateAbove100
            });

        return new TankType2VolumeDefinition().Calculate(parameters, includeTrace);
    }

    private static CalculationResult CalculateType3(double levelRaw, double densityHmi, double dimA, double dimB, double dimC, double dimD, double upperDeadArea, double lowerDeadArea, bool calculateAbove100, bool includeTrace = false)
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelRaw"] = levelRaw,
                ["densityHmi"] = densityHmi,
                ["dimA"] = dimA,
                ["dimB"] = dimB,
                ["dimC"] = dimC,
                ["dimD"] = dimD,
                ["upperDeadArea"] = upperDeadArea,
                ["lowerDeadArea"] = lowerDeadArea,
                ["calculateAbove100"] = calculateAbove100
            });

        return new TankType3VolumeDefinition().Calculate(parameters, includeTrace);
    }

    private static CalculationResult CalculateType4(double levelRaw, double densityHmi, double dimA, double dimB, double dimC, double upperDeadArea, double lowerDeadArea, bool calculateAbove100, bool includeTrace = false)
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelRaw"] = levelRaw,
                ["densityHmi"] = densityHmi,
                ["dimA"] = dimA,
                ["dimB"] = dimB,
                ["dimC"] = dimC,
                ["upperDeadArea"] = upperDeadArea,
                ["lowerDeadArea"] = lowerDeadArea,
                ["calculateAbove100"] = calculateAbove100
            });

        return new TankType4VolumeDefinition().Calculate(parameters, includeTrace);
    }

    private static CalculationResult CalculateType5(double levelRaw, double densityHmi, double dimA, double dimB, double dimC, double dimD, double dimE, double dimF, double upperDeadArea, double lowerDeadArea, bool calculateAbove100, bool includeTrace = false)
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelRaw"] = levelRaw,
                ["densityHmi"] = densityHmi,
                ["dimA"] = dimA,
                ["dimB"] = dimB,
                ["dimC"] = dimC,
                ["dimD"] = dimD,
                ["dimE"] = dimE,
                ["dimF"] = dimF,
                ["upperDeadArea"] = upperDeadArea,
                ["lowerDeadArea"] = lowerDeadArea,
                ["calculateAbove100"] = calculateAbove100
            });

        return new TankType5VolumeDefinition().Calculate(parameters, includeTrace);
    }

    private static CalculationResult CalculateType6(double levelRaw, double densityHmi, double dimA, double dimB, double dimC, double upperDeadArea, double lowerDeadArea, bool calculateAbove100, bool includeTrace = false)
    {
        var parameters = new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["levelRaw"] = levelRaw,
                ["densityHmi"] = densityHmi,
                ["dimA"] = dimA,
                ["dimB"] = dimB,
                ["dimC"] = dimC,
                ["upperDeadArea"] = upperDeadArea,
                ["lowerDeadArea"] = lowerDeadArea,
                ["calculateAbove100"] = calculateAbove100
            });

        return new TankType6VolumeDefinition().Calculate(parameters, includeTrace);
    }


    private static double GetOutput(CalculationResult result, string key)
    {
        return result.Outputs.Single(output => string.Equals(output.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}