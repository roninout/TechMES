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
            Description: "Legacy H_MAX = DistanceB - DistanceA."),

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
    public override string Version => "2";

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
            /*
             * Старый Level.Val_R.
             *
             * Позже TankConfigurationPanel автоматически привяжет сюда
             * реальный R-tag связанного Level.
             */
            Number(
                key: "levelRaw",
                name: "Level raw",
                unit: "",
                order: 1,
                defaultValue: 0d,
                minimum: null,
                description: "Linked Level.R value."),

            /*
             * Старый Density.ValHmi.
             *
             * Позже автоматически привязывается к связанному Density.
             */
            Number(
                key: "densityHmi",
                name: "Density HMI",
                unit: "",
                order: 2,
                defaultValue: 0d,
                minimum: null,
                description: "Linked Density HMI value.")
        };

        result.AddRange(geometryParameters);

        /*
         * Старый TankContent:
         *
         * distanceA
         * distanceB
         * distToDistanceA
         * probeLength
         *
         * Это обычные константы Job.
         */
        result.AddRange(
        [
            Number(
                key: "distanceA",
                name: "Distance A",
                unit: "mm",
                order: 90,
                defaultValue: 0d,
                minimum: 0d),

            Number(
                key: "distanceB",
                name: "Distance B",
                unit: "mm",
                order: 91,
                defaultValue: 0d,
                minimum: 0d),

            Number(
                key: "distToDistanceA",
                name: "Distance to A",
                unit: "mm",
                order: 92,
                defaultValue: 0d,
                minimum: 0d),

            /*
             * ProbeLength сохраняем как конфигурационный параметр.
             *
             * В старом Tank.cs он не участвует ни в одной
             * из восьми формул объёма.
             */
            Number(
                key: "probeLength",
                name: "Probe length",
                unit: "mm",
                order: 93,
                defaultValue: 0d,
                minimum: 0d,
                description: "Stored TankContent parameter. Not used by current Tank volume formulas.")
        ]);

        return result;
    }

    /// <summary>
    /// Создаёт геометрический размер dimA..dimF.
    /// </summary>
    protected static CalculationParameterDefinition Dimension(
        string key,
        string name,
        int order,
        string unit = "mm",
        double defaultValue = 0d,
        double? minimum = 0d,
        double step = 1d,
        int decimals = 0,
        string? description = null)
    {
        return Number(key, name, unit, order, defaultValue, minimum, step, decimals, description);
    }

    private static CalculationParameterDefinition Number(
        string key,
        string name,
        string unit,
        int order,
        double defaultValue,
        double? minimum,
        double step = 1d,
        int decimals = 0,
        string? description = null)
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
    /// Общий LevelTank pipeline.
    ///
    /// В CalculateVolume() передаётся уже рассчитанный levelMm,
    /// поэтому сами 8 геометрических алгоритмов менять не нужно.
    /// </summary>
    protected sealed override CalculationResult CalculateCore(
        CalculationParameterSet parameters,
        bool includeTrace)
    {
        var levelRaw = parameters.GetRequiredDouble("levelRaw");
        var densityHmi = parameters.GetRequiredDouble("densityHmi");
        var distanceA = parameters.GetRequiredDouble("distanceA");
        var distanceB = parameters.GetRequiredDouble("distanceB");

        var levelTank = LevelTankCalculator.Calculate(
            levelRaw,
            densityHmi,
            distanceA,
            distanceB,
            levelMm =>
            {
                /*
                 * Конкретные TankTypeN остались legacy-compatible
                 * и ожидают внутренний параметр levelMm.
                 *
                 * В Job его больше нет: это производное значение.
                 */
                var volumeParameters = parameters.Values.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase);

                volumeParameters["levelMm"] = levelMm;

                return CalculateVolume(new CalculationParameterSet(volumeParameters));
            });

        if (!double.IsFinite(levelTank.VolumeM3))
        {
            return CalculationResult.Failure(
                "tank.volume.not-finite",
                "Calculated tank volume is not a finite number.");
        }

        if (!double.IsFinite(levelTank.MassT))
        {
            return CalculationResult.Failure(
                "tank.mass.not-finite",
                "Calculated tank mass is not a finite number.");
        }

        IReadOnlyList<CalculationTraceItem> trace = includeTrace
            ?
            [
                new(
                    Key: "hMaxMm",
                    Name: "H max",
                    Value: levelTank.HMaxMm.ToString(CultureInfo.InvariantCulture),
                    Unit: "mm"),

                new(
                    Key: "levelMm",
                    Name: "Calculated level",
                    Value: levelTank.LevelMm.ToString(CultureInfo.InvariantCulture),
                    Unit: "mm"),

                new(
                    Key: "volumeM3",
                    Name: "Calculated volume",
                    Value: levelTank.VolumeM3.ToString("0.############", CultureInfo.InvariantCulture),
                    Unit: "m³"),

                new(
                    Key: "massT",
                    Name: "Calculated mass",
                    Value: levelTank.MassT.ToString("0.############", CultureInfo.InvariantCulture),
                    Unit: "t")
            ]
            :
            [];

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

    /// <summary>
    /// Legacy-формула конкретного Tank Type.
    ///
    /// Параметр levelMm сюда уже добавлен LevelTankCalculator-оболочкой.
    /// </summary>
    protected abstract double CalculateVolume(CalculationParameterSet parameters);
}