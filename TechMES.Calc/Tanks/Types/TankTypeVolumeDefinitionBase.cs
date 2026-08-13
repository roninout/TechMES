using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// Общая база production Tank Type definitions.
///
/// Каждый геометрический тип сборника имеет собственный DefinitionCode
/// и находится в отдельном файле.
///
/// Благодаря этому добавление нового типа Tank не требует изменения
/// общей модели Calc Job, Runtime API или PostgreSQL.
/// </summary>
public abstract class TankTypeVolumeDefinitionBase : CalculationDefinitionBase
{
    /// <summary>
    /// Общий выход для всех Tank-алгоритмов.
    /// </summary>
    private static readonly IReadOnlyList<CalculationOutputDefinition>
        OutputDefinitions =
        [
            new(
                Key: "volume",
                Name: "Volume",
                Unit: "m³",
                Decimals: 3,
                Order: 1,
                Description: "Calculated liquid volume.")
        ];

    /// <summary>
    /// Все Tank Types относятся к одной категории.
    /// WEB будет использовать её для формирования списка Tank algorithms.
    /// </summary>
    public override string Category => "Tanks";

    /// <summary>
    /// Версия математического поведения алгоритма.
    /// </summary>
    public override string Version => "1";

    /// <summary>
    /// Общий выход всех Tank Types.
    /// </summary>
    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    /// <summary>
    /// Создаёт общий набор параметров Tank.
    ///
    /// Сначала добавляется текущий уровень,
    /// затем размеры конкретного Tank Type,
    /// затем параметры измерительной части датчика.
    ///
    /// Для параметров предусмотрены DefaultValue.
    /// Это позволяет сначала создать выключенный Job,
    /// а геометрию и реальные Tag bindings настроить следующим этапом.
    /// </summary>
    protected static IReadOnlyList<CalculationParameterDefinition>CreateParameters(params CalculationParameterDefinition[] geometryParameters)
    {
        var result = new List<CalculationParameterDefinition>
        {
            Number(
                key: "levelMm",
                name: "Measured level",
                unit: "mm",
                order: 1,
                defaultValue: 0d,
                minimum: null,
                description:
                    "Current measured level from the level sensor.")
        };

        /*
         * Размеры конкретного Tank Type:
         *
         * dimA
         * dimB
         * dimC
         * ...
         */
        result.AddRange(geometryParameters);

        /*
         * Общая измерительная часть.
         *
         * Эти названия соответствуют существующей legacy-модели Tank:
         *
         * DistanceA
         * DistanceB
         * DistToDistanceA / ltoDistanceA
         * ProbeLength
         */
        result.AddRange(
        [
            Number(
                key: "distanceA",
                name: "Distance A",
                unit: "mm",
                order: 90,
                defaultValue: 0d,
                description:
                    "Legacy TankContent.DistanceA."),

            Number(
                key: "distanceB",
                name: "Distance B",
                unit: "mm",
                order: 91,
                defaultValue: 0d,
                description:
                    "Legacy TankContent.DistanceB."),

            Number(
                key: "distToDistanceA",
                name: "Distance to A",
                unit: "mm",
                order: 92,
                defaultValue: 0d,
                description:
                    "Legacy TankContent.DistToDistanceA / ltoDistanceA."),

            Number(
                key: "probeLength",
                name: "Probe length",
                unit: "mm",
                order: 93,
                defaultValue: 0d,
                description:
                    "Legacy TankContent.ProbeLength.")
        ]);

        return result;
    }

    /// <summary>
    /// Создаёт геометрический размер Tank.
    /// По умолчанию размер задаётся в mm.
    /// </summary>
    protected static CalculationParameterDefinition Dimension(
        string key,
        string name,
        int order,
        string? description = null,
        string unit = "mm",
        double defaultValue = 1d,
        double minimum = 0d,
        double step = 1d,
        int decimals = 0)
    {
        return Number(
            key,
            name,
            unit,
            order,
            defaultValue,
            minimum,
            description,
            step,
            decimals);
    }

    /// <summary>
    /// Создаёт числовой параметр Calculation Definition.
    /// </summary>
    protected static CalculationParameterDefinition Number(
        string key,
        string name,
        string unit,
        int order,
        double defaultValue,
        double? minimum = 0d,
        string? description = null,
        double step = 1d,
        int decimals = 0)
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
    /// Временное поведение для Tank Types,
    /// точная legacy-формула которых ещё не перенесена.
    ///
    /// ВАЖНО:
    /// специально НЕ выполняем приблизительный расчёт.
    ///
    /// Новый Job создаётся Disabled, поэтому наличие Definition
    /// позволяет уже сейчас создавать и конфигурировать Job,
    /// не рискуя получить неправильный производственный результат.
    /// </summary>
    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        _ = parameters;
        _ = includeTrace;

        return CalculationResult.Failure("tank.algorithm.pending", $"{Name} is available for Job configuration, " + "but its exact legacy formula has not been ported yet.");
    }
}