using System.Globalization;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Tanks.Types;

/// <summary>
/// Общая база всех алгоритмов расчёта объёма Tank.
///
/// Каждый тип сборника реализует свою формулу
/// в отдельном TankTypeNVolumeDefinition.cs.
///
/// Общая база содержит только:
/// - описание общих входных параметров;
/// - описание выхода Volume;
/// - преобразование результата в CalculationResult.
///
/// Математика конкретного типа здесь отсутствует.
/// </summary>
public abstract class TankTypeVolumeDefinitionBase : CalculationDefinitionBase
{
    private static readonly IReadOnlyList<CalculationOutputDefinition>
        OutputDefinitions =
        [
            new(
                Key: "volume",
                Name: "Volume",
                Unit: "m³",
                Decimals: 3,
                Order: 1,
                Description: "Calculated tank volume.")
        ];

    public override string Category => "Tanks";

    public override string Version => "1";

    public override IReadOnlyList<CalculationOutputDefinition>Outputs => OutputDefinitions;

    /// <summary>
    /// Создаёт полный набор параметров конкретного Tank Type.
    ///
    /// geometryParameters содержат только используемые данным типом
    /// dimA..dimF.
    ///
    /// Остальные параметры являются общими для всех Tank.
    /// </summary>
    protected static IReadOnlyList<CalculationParameterDefinition>CreateParameters(params CalculationParameterDefinition[] geometryParameters)
    {
        var result = new List<CalculationParameterDefinition>
        {
            /*
             * Текущее значение уровня.
             *
             * В production это будет Tag input.
             * Пока default 0 позволяет создать Disabled Job
             * до окончательной настройки привязок.
             */
            Number(
                key: "levelMm",
                name: "Measured level",
                unit: "mm",
                order: 1,
                defaultValue: 0d,
                minimum: null,
                description: "Current measured level.")
        };

        result.AddRange(geometryParameters);

        /*
         * Параметры измерительной части.
         *
         * Названия соответствуют старым:
         *
         * TankContent.distanceA
         * TankContent.distanceB
         * TankContent.distToDistanceA
         * TankContent.probeLength
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
             * В старом Tank.cs ProbeLength не участвует
             * ни в одном из 8 алгоритмов.
             *
             * Оставляем его в конфигурации,
             * но НЕ придумываем ему математическое применение.
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
    /// Создаёт размер dimA..dimF.
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
        return Number(
            key,
            name,
            unit,
            order,
            defaultValue,
            minimum,
            step,
            decimals,
            description);
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
    /// Общая оболочка выполнения Tank-алгоритма.
    /// Конкретная формула находится в CalculateVolume().
    /// </summary>
    protected sealed override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var volumeM3 = CalculateVolume(parameters);

        if (!double.IsFinite(volumeM3))
        {
            return CalculationResult.Failure("tank.volume.not-finite", "Calculated tank volume is not a finite number.");
        }

        IReadOnlyList<CalculationTraceItem> trace =
            includeTrace
                ?
                [
                    new CalculationTraceItem(
                        Key: "volumeM3",
                        Name: "Calculated volume",
                        Value: volumeM3.ToString(
                            "0.############",
                            CultureInfo.InvariantCulture),
                        Unit: "m³")
                ]
                :
                [];

        return CalculationResult.Success(
            outputs:
            [
                new CalculationOutput(
                    Key: "volume",
                    Name: "Volume",
                    Value: volumeM3,
                    Unit: "m³")
            ],
            trace: trace);
    }

    /// <summary>
    /// Формула конкретного Tank Type.
    /// </summary>
    protected abstract double CalculateVolume(CalculationParameterSet parameters);
}