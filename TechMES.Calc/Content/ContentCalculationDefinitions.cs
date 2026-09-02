using TechMES.Calc.Abstractions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Constants;

namespace TechMES.Calc.Content;

/// <summary>
/// Calculation Definitions для всех поддерживаемых Content-систем.
///
/// Каждый Definition представляет одну физическую систему. Компоненты внутри него фиксированы намеренно:
///
/// - пользователь не может собрать неподдерживаемую комбинацию;
/// - Calc Job однозначно идентифицирует используемую корреляцию;
/// - каждый output имеет стабильный семантический ключ;
/// - WEB может полностью построить редактор по metadata Definition.
///
/// Все определения используют один и тот же production facade: ContentPropertyCalculator.
/// </summary>
public static class ContentCalculationDefinitions
{
    public const string AcnWaterCode = "content.acn-water";
    public const string PoPropyleneCode = "content.po-propylene";
    public const string PoWaterCode = "content.po-water";
    public const string AcaPoCode = "content.aca-po";
    public const string AlcWaterCode = "content.alc-water";
    public const string AcnWaterPoCode = "content.acn-water-po";

    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarGauge";
    private const string ConfigurationCodeKey = "configurationCode";
    private const string SelectedContentItemIndexKey = "selectedContentItemIndex";

    private const int MaxContentItemCount = 5;

    private static string PressureDeltaKey(int index) => $"component{index}PressureDelta";
    private static string TemperatureDeltaKey(int index) => $"component{index}TemperatureDelta";

    /// <summary>
    /// Параметры Content.
    ///
    /// Temperature:
    ///     фактическая температура процесса, °C.
    ///
    /// Pressure:
    ///     измеренное SCADA Pressure.R, bar(g).
    ///
    /// configurationCode:
    ///     Content.Conf.
    ///
    /// selectedContentItemIndex:
    ///     Content.Select:
    ///
    ///         0 -> Param0
    ///         1 -> Param1
    ///         ...
    ///         4 -> Param4
    ///
    /// Для выбранного ParamN используются соответствующие:
    ///
    ///     ParamN_Dp
    ///     ParamN_Dt
    ///
    /// После чего физические входы Content-корреляции формируются:
    ///
    ///     T(calc) = T + dT
    ///
    ///     P(corrected,g) = P(g) + dP
    ///
    ///     P(abs) = P(corrected,g)
    ///              + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute
    ///
    /// Атмосферное давление нигде локально не дублируется.
    /// </summary>
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameterDefinitions();

    private static IReadOnlyList<CalculationParameterDefinition> CreateParameterDefinitions()
    {
        var result = new List<CalculationParameterDefinition>
        {
            new(
                Key: TemperatureKey,
                Name: "Temperature",
                Type: CalculationParameterType.Number,
                Unit: "°C",
                IsRequired: true,
                Step: 0.1,
                Decimals: 2,
                Order: 1,
                Description: "Process temperature before Content dT correction.",
                Role: CalculationParameterRole.ProcessInput),

            new(
                Key: PressureKey,
                Name: "Pressure",
                Type: CalculationParameterType.Number,
                Unit: "bar(g)",
                IsRequired: true,
                Step: 0.01,
                Decimals: 4,
                Order: 2,
                Description: "Gauge process pressure before Content dP correction.",
                Role: CalculationParameterRole.ProcessInput),

            new(
                Key: ConfigurationCodeKey,
                Name: "Configuration code",
                Type: CalculationParameterType.Integer,
                IsRequired: true,
                Order: 3,
                Description: "Content.Conf legacy correlation configuration code.",
                Role: CalculationParameterRole.Configuration),

            new(
                Key: SelectedContentItemIndexKey,
                Name: "Selected Content item",
                Type: CalculationParameterType.Integer,
                IsRequired: true,
                Minimum: 0d,
                Maximum: MaxContentItemCount - 1d,
                Order: 4,
                Description: "Content.Select. Selects ParamN and its ParamN_Dp / ParamN_Dt corrections.",
                Role: CalculationParameterRole.Configuration)
        };

        for (var index = 0; index < MaxContentItemCount; index++)
        {
            result.Add(new CalculationParameterDefinition(
                Key: PressureDeltaKey(index),
                Name: $"Param{index} dP",
                Type: CalculationParameterType.Number,
                Unit: "bar",
                IsRequired: false,
                DefaultValue: 0d,
                Step: 0.01,
                Decimals: 2,
                Order: 10 + index * 2,
                Description: $"Pressure correction read from Content.Param{index}_Dp.",
                Role: CalculationParameterRole.Configuration));

            result.Add(new CalculationParameterDefinition(
                Key: TemperatureDeltaKey(index),
                Name: $"Param{index} dT",
                Type: CalculationParameterType.Number,
                Unit: "°C",
                IsRequired: false,
                DefaultValue: 0d,
                Step: 0.1,
                Decimals: 1,
                Order: 11 + index * 2,
                Description: $"Temperature correction read from Content.Param{index}_Dt.",
                Role: CalculationParameterRole.Configuration));
        }

        return result;
    }

    /// <summary>
    /// Создаёт все встроенные Content Definitions.
    /// </summary>
    public static IReadOnlyList<ICalculationDefinition> CreateAll()
    {
        return
        [
            new ContentCalculationDefinition(
                AcnWaterCode,
                "ACN / Water content",
                [
                    new ContentComponent("ACN", "acnPercent", "ACN"),
                    new ContentComponent("Water", "waterPercent", "Water")
                ]),

            new ContentCalculationDefinition(
                PoPropyleneCode,
                "PO / Propylene content",
                [
                    new ContentComponent("PO", "poPercent", "PO"),
                    new ContentComponent("P", "pPercent", "Propylene")
                ]),

            new ContentCalculationDefinition(
                PoWaterCode,
                "PO / Water content",
                [
                    new ContentComponent("PO", "poPercent", "PO"),
                    new ContentComponent("Water", "waterPercent", "Water")
                ]),

            new ContentCalculationDefinition(
                AcaPoCode,
                "ACA / PO content",
                [
                    new ContentComponent("ACA", "acaPercent", "ACA"),
                    new ContentComponent("PO", "poPercent", "PO")
                ]),

            new ContentCalculationDefinition(
                AlcWaterCode,
                "Alcohol / Water content",
                [
                    new ContentComponent("ALC", "alcPercent", "Alcohol"),
                    new ContentComponent("Water", "waterPercent", "Water")
                ]),

            new ContentCalculationDefinition(
                AcnWaterPoCode,
                "ACN / Water / PO content",
                [
                    new ContentComponent("ACN", "acnPercent", "ACN"),
                    new ContentComponent("Water", "waterPercent", "Water"),
                    new ContentComponent("PO", "poPercent", "PO")
                ])
        ];
    }

    /// <summary>
    /// Один компонент физической Content-системы.
    ///
    /// SubstanceCode используется математическим ядром.
    /// OutputKey является стабильным ключом Calc output.
    /// OutputName используется в WEB/diagnostics.
    /// </summary>
    private sealed record ContentComponent(string SubstanceCode, string OutputKey, string OutputName);

    /// <summary>
    /// Универсальная реализация Calculation Definition.
    ///
    /// Сам Definition не содержит физической математики.
    /// Он только связывает инфраструктуру Calculation Definition
    /// с уже протестированным ContentPropertyCalculator.
    /// </summary>
    private sealed class ContentCalculationDefinition : CalculationDefinitionBase
    {
        private readonly string _code;
        private readonly string _name;
        private readonly ContentComponent[] _components;
        private readonly string[] _componentCodes;
        private readonly IReadOnlyList<CalculationOutputDefinition> _outputDefinitions;

        public ContentCalculationDefinition(string code, string name, ContentComponent[] components)
        {
            _code = code;
            _name = name;
            _components = components;
            _componentCodes = components.Select(component => component.SubstanceCode).ToArray();

            _outputDefinitions = components
                .Select((component, index) => new CalculationOutputDefinition(
                    Key: component.OutputKey,
                    Name: $"{component.OutputName} content",
                    Unit: "%",
                    Decimals: 2,
                    Order: index + 1,
                    Description: $"Calculated {component.OutputName} content in engineering percent."))
                .ToArray();
        }

        public override string Code => _code;
        public override string Name => _name;
        public override string Category => "Content";
        public override string Version => "5";

        public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;
        public override IReadOnlyList<CalculationOutputDefinition> Outputs => _outputDefinitions;

        protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
        {
            var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
            var pressureBarGauge = parameters.GetRequiredDouble(PressureKey);
            var configurationCode = parameters.GetRequiredInt(ConfigurationCodeKey);
            var selectedContentItemIndex = parameters.GetRequiredInt(SelectedContentItemIndexKey);

            if (selectedContentItemIndex < 0 || selectedContentItemIndex >= _components.Length)
                return CalculationResult.Failure("content.select.invalid", $"Content.Select={selectedContentItemIndex} is outside the current Content system range 0..{_components.Length - 1}.");

            // ------------------------------------------------------------
            // Коррекции активного Content ParamN.
            //
            // На уровне Calculation Definition параметры уже имеют
            // нормальный физический смысл:
            //
            //     PressureDelta    = dP
            //     TemperatureDelta = dT
            //
            // Legacy SCADA mapping выполняет WEB:
            //
            //     dP <- ParamN_Dt
            //     dT <- ParamN_Dp
            // ------------------------------------------------------------

            var pressureDeltaBar = parameters.GetDouble(PressureDeltaKey(selectedContentItemIndex), 0d);
            var temperatureDeltaC = parameters.GetDouble(TemperatureDeltaKey(selectedContentItemIndex), 0d);

            // ------------------------------------------------------------
            // Скорректированные технологические параметры.
            // ------------------------------------------------------------

            var effectiveTemperatureC = temperatureC + temperatureDeltaC;
            var effectivePressureBarGauge = pressureBarGauge + pressureDeltaBar;
            var effectivePressureBarAbsolute = effectivePressureBarGauge + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;

            // ------------------------------------------------------------
            // Физический Content calculation.
            // ------------------------------------------------------------

            var percentages = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(Components: _componentCodes, TemperatureC: effectiveTemperatureC, PressureBarAbsolute: effectivePressureBarAbsolute, ConfigurationCode: configurationCode));
            var outputs = new CalculationOutput[_components.Length];

            for (var index = 0; index < _components.Length; index++)
            {
                var component = _components[index];
                outputs[index] = new CalculationOutput(Key: component.OutputKey, Name: $"{component.OutputName} content", Value: percentages[index], Unit: "%");
            }

            if (!includeTrace)
                return CalculationResult.Success(outputs);

            // ------------------------------------------------------------
            // Trace специально делаем подробным.
            //
            // Тогда сразу увидим:
            //
            //     Pressure.R
            //     Atmospheric Pressure
            //     dP
            //     конечный P(abs)
            //
            // и то же самое для Temperature.
            // ------------------------------------------------------------

            var trace = new List<CalculationTraceItem>
            {
                new("temperatureC", "Temperature", Format(temperatureC), "°C"),
                new("temperatureDeltaC", $"Param{selectedContentItemIndex} dT", Format(temperatureDeltaC), "°C"),
                new("effectiveTemperatureC", "Effective temperature", Format(effectiveTemperatureC), "°C"),
                new("pressureBarGauge", "Pressure", Format(pressureBarGauge), "bar(g)"),
                new("pressureDeltaBar", $"Param{selectedContentItemIndex} dP", Format(pressureDeltaBar), "bar"),
                new("atmosphericPressureBarAbsolute", "Atmospheric pressure", Format(CalculationPhysicalConstants.AtmosphericPressureBarAbsolute), "bar"),
                new("effectivePressureBarAbsolute", "Effective absolute pressure", Format(effectivePressureBarAbsolute), "bar(abs)"),
                new("selectedContentItemIndex", "Content Select", selectedContentItemIndex.ToString(), null),
                new("configurationCode", "Configuration code", configurationCode.ToString(), null)
            };

            for (var index = 0; index < _components.Length; index++)
            {
                var component = _components[index];
                trace.Add(new CalculationTraceItem(component.OutputKey, $"{component.OutputName} content", Format(percentages[index]), "%"));
            }

            return CalculationResult.Success(outputs, trace: trace);
        }

        private static string Format(double value)
        {
            return value.ToString("0.###############", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}