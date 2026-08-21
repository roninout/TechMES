using System.Globalization;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// Общая база всех алгоритмов Tank.
///
/// Конкретная геометрическая формула по-прежнему находится
/// в отдельном TankTypeNVolumeDefinition.cs.
///
/// Этот класс теперь выполняет общую legacy-логику LevelTank:
///
/// Level raw -> LevelMm -> Tank Volume -> Mass.
/// </summary>
public abstract class TankTypeVolumeDefinitionBase : CalculationDefinitionBase
{
    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new(
            Key: "hMax",
            Name: "H max",
            Unit: "mm",
            Decimals: 0,
            Order: 1,
            Description: "Sensor measurement area height."),

        new(
            Key: "levelMm",
            Name: "Level",
            Unit: "mm",
            Decimals: 0,
            Order: 2,
            Description: "Calculated liquid level in millimetres."),

        new(
            Key: "volume",
            Name: "Volume",
            Unit: "m³",
            Decimals: 3,
            Order: 3,
            Description: "Calculated tank volume."),

        new(
            Key: "mass",
            Name: "Mass",
            Unit: "t",
            Decimals: 3,
            Order: 4,
            Description: "Calculated product mass.")
    ];

    public override string Category => "Tanks";

     // Меняем версию, потому что контракт алгоритма теперь другой:
     //
     // раньше: levelMm -> volume
     // теперь: levelRaw + densityHmi -> hMax + levelMm + volume + mass
    public override string Version => "3";

    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Создаёт полный набор параметров конкретного Tank Type.
    ///
    /// levelRaw и densityHmi являются связанными Runtime inputs.
    /// В специализированном Tank UI пользователь их редактировать не будет.
    ///
    /// geometryParameters и параметры измерительной части являются
    /// константами Job.
    /// </summary>
    protected static IReadOnlyList<CalculationParameterDefinition> CreateParameters(params CalculationParameterDefinition[] geometryParameters)
    {
        var result = new List<CalculationParameterDefinition>
    {
        Number("levelRaw", "Level raw", "%", 1, 0d, null, description: "Linked Level.R value."),
        Number("densityHmi", "Density HMI", "kg/m³", 2, 0d, null, description: "Linked Density HMI value.")
    };

        result.AddRange(geometryParameters);

        // Новая модель измерительной части.
        result.AddRange(
        [
            Number("upperDeadArea", "Upper dead area", "mm", 90, 150d, 0d, description: "Unmeasured area above the sensor working range."),
            Number("lowerDeadArea", "Lower dead area", "mm", 91, 150d, 0d, description: "Unmeasured area below the sensor working range."),
            Boolean("calculateAbove100", "Calculate above 100%", 92, false, "Continue volume calculation above 100% inside the upper dead area.")
        ]);

        return result;
    }

    private static CalculationParameterDefinition Boolean(string key, string name, int order, bool defaultValue, string? description = null)
    {
        return new CalculationParameterDefinition(
            Key: key,
            Name: name,
            Type: CalculationParameterType.Boolean,
            Unit: null,
            IsRequired: true,
            DefaultValue: defaultValue,
            Order: order,
            Description: description);
    }

    /// <summary>
    /// Создаёт геометрический размер dimA..dimF.
    /// </summary>
    protected static CalculationParameterDefinition Dimension(string key, string name, int order, string unit = "mm", double? defaultValue = null, double? minimum = 0d, double step = 1d, int decimals = 0, string? description = null)
    {
        var resolvedDefaultValue = defaultValue ?? GetDefaultDimensionValue(key);

        return Number(key, name, unit, order, resolvedDefaultValue, minimum, step, decimals, description);
    }

    /// <summary>
    /// Начальные геометрические размеры нового Tank Job.
    ///
    /// Эти значения используются только при создании новой конфигурации.
    /// Для существующего Job всегда загружаются сохранённые ConstantValue.
    /// </summary>
    private static double GetDefaultDimensionValue(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "dima" => 2500d,
            "dimb" => 1600d,
            "dimc" => 400d,
            "dimd" => 350d,
            "dime" => 0d,
            "dimf" => 0d,
            _ => 0d
        };
    }

    private static CalculationParameterDefinition Number(string key, string name, string unit, int order, double defaultValue, double? minimum, double step = 1d, int decimals = 0, string? description = null)
    {
        return new CalculationParameterDefinition(
            Key: key,
            Name: name,
            Type: CalculationParameterType.Number,
            Unit: unit,
            IsRequired: true,
            DefaultValue: defaultValue,
            Minimum: minimum,
            Step: step,
            Decimals: decimals,
            Order: order,
            Description: description);
    }

    /// <summary>
    /// Полный физический размер Tank по направлению измерения уровня.
    ///
    /// Для вертикального Tank это высота.
    /// Для горизонтального Tank это внутренний диаметр по вертикали.
    /// </summary>
    protected abstract double GetTotalLengthMm(CalculationParameterSet parameters);

    /// <summary>
    /// Общий LevelTank pipeline.
    ///
    /// В CalculateVolume() передаётся уже рассчитанный levelMm,
    /// поэтому сами 8 геометрических алгоритмов менять не нужно.
    /// </summary>
    protected sealed override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var levelRaw = parameters.GetRequiredDouble("levelRaw");
        var densityHmi = parameters.GetRequiredDouble("densityHmi");
        var upperDeadArea = parameters.GetRequiredDouble("upperDeadArea");
        var lowerDeadArea = parameters.GetRequiredDouble("lowerDeadArea");
        var calculateAbove100 = parameters.GetRequiredBoolean("calculateAbove100");
        var totalLengthMm = GetTotalLengthMm(parameters);

        if (!double.IsFinite(totalLengthMm) || totalLengthMm <= 0)
            return CalculationResult.Failure("tank.geometry.invalid-total-length", "Tank total length must be greater than zero.");

        if (upperDeadArea < 0 || lowerDeadArea < 0)
            return CalculationResult.Failure("tank.sensor.invalid-dead-area", "Tank dead areas cannot be negative.");

        var measurementAreaMm = totalLengthMm - lowerDeadArea - upperDeadArea;

        if (measurementAreaMm <= 0)
            return CalculationResult.Failure("tank.sensor.invalid-measurement-area", "Lower dead area and upper dead area leave no valid sensor measurement area.");


        // Общая LevelTank-логика находится здесь, потому что она одинакова для всех Tank Type.
        // Конкретный Tank Type получает уже физическую высоту жидкости от самого дна Tank.
        var levelTank = LevelTankCalculator.Calculate(levelRaw, densityHmi, totalLengthMm, lowerDeadArea, upperDeadArea, calculateAbove100, liquidHeightMm =>
            {
                // Type 8 пока ещё используют compatibility parameters.
                // После перевода всех геометрий на новую точную модель distance можно будет удалить полностью.
                var volumeParameters = parameters.Values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

                volumeParameters["levelMm"] = liquidHeightMm;
                volumeParameters["distanceA"] = 0d;
                volumeParameters["distanceB"] = totalLengthMm;
                volumeParameters["distToDistanceA"] = 0d;

                return CalculateVolume(new CalculationParameterSet(volumeParameters));
            });

        if (!double.IsFinite(levelTank.VolumeM3))
            return CalculationResult.Failure("tank.volume.not-finite", "Calculated tank volume is not a finite number.");

        if (!double.IsFinite(levelTank.MassT))
            return CalculationResult.Failure("tank.mass.not-finite", "Calculated tank mass is not a finite number.");

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            // ============================================================
            // Обычные диагностические значения.
            // ============================================================

            trace.Add(
                new CalculationTraceItem(
                    "totalLengthMm",
                    "Tank total length",
                    totalLengthMm.ToString("0.############", CultureInfo.InvariantCulture),
                    "mm"));

            trace.Add(
                new CalculationTraceItem(
                    "upperDeadAreaMm",
                    "Upper dead area",
                    upperDeadArea.ToString("0.############", CultureInfo.InvariantCulture),
                    "mm"));

            trace.Add(
                new CalculationTraceItem(
                    "measurementAreaMm",
                    "Measurement area",
                    levelTank.HMaxMm.ToString("0.############", CultureInfo.InvariantCulture),
                    "mm"));

            trace.Add(
                new CalculationTraceItem(
                    "lowerDeadAreaMm",
                    "Lower dead area",
                    lowerDeadArea.ToString("0.############", CultureInfo.InvariantCulture),
                    "mm"));

            trace.Add(
                new CalculationTraceItem(
                    "levelMm",
                    "Measured level",
                    levelTank.LevelMm.ToString("0.############", CultureInfo.InvariantCulture),
                    "mm"));

            trace.Add(
                new CalculationTraceItem(
                    "liquidHeightMm",
                    "Physical liquid height",
                    levelTank.LiquidHeightMm.ToString("0.############", CultureInfo.InvariantCulture),
                    "mm"));

            trace.Add(
                new CalculationTraceItem(
                    "volumeM3",
                    "Calculated volume",
                    levelTank.VolumeM3.ToString("0.############", CultureInfo.InvariantCulture),
                    "m³"));

            trace.Add(
                new CalculationTraceItem(
                    "massT",
                    "Calculated mass",
                    levelTank.MassT.ToString("0.############", CultureInfo.InvariantCulture),
                    "t"));


            // ============================================================
            // Общая Help-информация измерительной части.
            // Эти формулы одинаковы для всех Tank Type, поэтому они находятся в Base, а не копируются восемь раз.
            // ============================================================

            trace.Add(new CalculationTraceItem("help.sensor.measurement.formula",
                "Measurement area formula",
                "Hmeas = Htotal - HupperDead - HlowerDead"));

            trace.Add(new CalculationTraceItem("help.sensor.measurement.calculation",
                "Measurement area calculation",
                $"{F(totalLengthMm)} - {F(upperDeadArea)} - {F(lowerDeadArea)} = {F(measurementAreaMm)} mm"));

            trace.Add(new CalculationTraceItem("help.sensor.level.formula",
                "Measured level formula",
                "Hlevel = max(0, Hmeas × Level / 100)"));

            trace.Add(new CalculationTraceItem("help.sensor.level.calculation",
                "Measured level calculation",
                $"{F(measurementAreaMm)} × {F(levelRaw)} / 100 = {F(levelTank.LevelMm)} mm"));
            
            var rawLiquidHeightMm = lowerDeadArea + levelTank.LevelMm;
            var volumeLimitMm = calculateAbove100 ? totalLengthMm : lowerDeadArea + measurementAreaMm;

            trace.Add(new CalculationTraceItem("help.sensor.liquid-height.formula",
                "Raw liquid height formula",
                "Hraw = HlowerDead + Hlevel"));

            trace.Add(new CalculationTraceItem("help.sensor.liquid-height.calculation",
                "Raw liquid height calculation",
                $"{F(lowerDeadArea)} + {F(levelTank.LevelMm)} = {F(rawLiquidHeightMm)} mm"));

            trace.Add(new CalculationTraceItem("help.sensor.volume-height.formula",
                "Volume calculation height",
                "Hvolume = min(Hraw, Hlimit)"));

            trace.Add(new CalculationTraceItem("help.sensor.volume-height.calculation",
                "Volume calculation height",
                $"min({F(rawLiquidHeightMm)}, {F(volumeLimitMm)}) = {F(levelTank.LiquidHeightMm)} mm"));

            if (!calculateAbove100)
            {
                trace.Add(new CalculationTraceItem("help.sensor.above100", "Above 100%", "Volume calculation is limited at the upper boundary of the measurement area."));
            }
            else
            {
                trace.Add(new CalculationTraceItem("help.sensor.above100", "Above 100%", "Volume calculation may continue inside the upper dead area, but never above the physical Tank geometry."));
            }


            // ============================================================
            // Здесь конкретный Tank Type добавляет СВОИ:
            //
            // - геометрические формулы;
            // - промежуточные объёмы;
            // - реальные подстановки;
            // - пояснение текущей области заполнения.
            //
            // Type 1 -> TankType1VolumeDefinition
            // Type 2 -> TankType2VolumeDefinition
            // ...
            // ============================================================

            trace.AddRange(BuildVolumeTrace(parameters, levelTank.LiquidHeightMm));

            // ============================================================
            // Mass общая для всех Tank Type.
            // ============================================================

            if (densityHmi > 0)
            {
                trace.Add(new CalculationTraceItem("help.result.mass.formula",
                    "Mass formula",
                    "Mass = Volume × Density × 0.001"));

                trace.Add(new CalculationTraceItem("help.result.mass.calculation",
                    "Mass calculation",
                    $"{F(levelTank.VolumeM3)} × {F(densityHmi)} × 0.001 = {F(levelTank.MassT)} t"));
            }
            else
            {
                trace.Add(new CalculationTraceItem("help.result.mass.formula",
                    "Mass formula",
                    "Density must be greater than zero. Otherwise Mass = 0."));

                trace.Add(new CalculationTraceItem("help.result.mass.calculation",
                    "Mass calculation",
                    $"Density = {F(densityHmi)} kg/m³, therefore Mass = {F(levelTank.MassT)} t"));
            }
        }

        return CalculationResult.Success(
            outputs:
            [
                new("hMax", "H max", levelTank.HMaxMm, "mm"),
                new("levelMm", "Level", levelTank.LevelMm, "mm"),
                new("volume", "Volume", levelTank.VolumeM3, "m³"),
                new("mass", "Mass", levelTank.MassT, "t")
            ],
            trace: trace);
    }

    // ============================================================
    // Type-specific Help.
    // По умолчанию конкретный Tank Type может ничего не возвращать.
    // Благодаря этому Type 2..8 продолжат компилироваться, пока мы будем переводить их на новый Help по одному.
    // ============================================================
    protected virtual IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        return [];
    }

    // ============================================================
    // Геометрический расчёт конкретного Tank Type.
    // ============================================================
    protected abstract double CalculateVolume(CalculationParameterSet parameters);

    // ============================================================
    // Единое форматирование чисел для Help.
    // ============================================================
    protected static string F(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}