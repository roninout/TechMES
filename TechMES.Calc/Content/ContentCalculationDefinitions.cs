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

    /// <summary>
    /// Общие входные параметры всех Content-корреляций.
    ///
    /// Temperature:
    ///     фактическая температура процесса, °C.
    ///
    /// Pressure:
    ///     измеренное SCADA Pressure.R, bar(g).
    ///
    /// Перед вызовом физической Content-корреляции давление переводится
    /// в абсолютное тем же способом, что и в Density:
    ///
    ///     P(abs) = P(g) + Patm.
    ///
    /// ConfigurationCode:
    ///     read-only Content.Conf.
    /// </summary>
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions =
    [
        new CalculationParameterDefinition(
        Key: TemperatureKey, Name: "Temperature", Type: CalculationParameterType.Number, Unit: "°C",
        IsRequired: true, Step: 0.1, Decimals: 2, Order: 1,
        Description: "Process temperature used by the Content correlation.",
        Role: CalculationParameterRole.ProcessInput),

    new CalculationParameterDefinition(
        Key: PressureKey, Name: "Pressure", Type: CalculationParameterType.Number, Unit: "bar(g)",
        IsRequired: true, Step: 0.01, Decimals: 4, Order: 2,
        Description: "Gauge process pressure. Absolute pressure is calculated by adding atmospheric pressure before the Content correlation.",
        Role: CalculationParameterRole.ProcessInput),

    new CalculationParameterDefinition(
        Key: ConfigurationCodeKey, Name: "Configuration code", Type: CalculationParameterType.Integer,
        IsRequired: true, Order: 3,
        Description: "Legacy-compatible Content correlation configuration code.",
        Role: CalculationParameterRole.Configuration)
    ];

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
        public override string Version => "3";

        public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;
        public override IReadOnlyList<CalculationOutputDefinition> Outputs => _outputDefinitions;

        protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
        {
            var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
            var pressureBarGauge = parameters.GetRequiredDouble(PressureKey);
            var pressureBarAbsolute = pressureBarGauge + CalculationPhysicalConstants.AtmosphericPressureBarAbsolute;
            var configurationCode = parameters.GetRequiredInt(ConfigurationCodeKey);

            var percentages = ContentPropertyCalculator.CalculatePercent(new ContentCalculationRequest(
                Components: _componentCodes,
                TemperatureC: temperatureC,
                PressureBarAbsolute: pressureBarAbsolute,
                ConfigurationCode: configurationCode));

            var outputs = new CalculationOutput[_components.Length];

            for (var index = 0; index < _components.Length; index++)
            {
                var component = _components[index];

                outputs[index] = new CalculationOutput(
                    Key: component.OutputKey,
                    Name: $"{component.OutputName} content",
                    Value: percentages[index],
                    Unit: "%");
            }

            if (!includeTrace)
                return CalculationResult.Success(outputs);

            var trace = new List<CalculationTraceItem>
                {
                    new("temperatureC", "Temperature", Format(temperatureC), "°C"),
                    new("pressureBarGauge", "Pressure", Format(pressureBarGauge), "bar(g)"),
                    new("pressureBarAbsolute", "Absolute pressure", Format(pressureBarAbsolute), "bar(abs)"),
                    new("configurationCode", "Configuration code", configurationCode.ToString(), null)
                };

            for (var index = 0; index < _components.Length; index++)
            {
                var component = _components[index];

                trace.Add(new CalculationTraceItem(
                    component.OutputKey,
                    $"{component.OutputName} content",
                    Format(percentages[index]),
                    "%"));
            }

            return CalculationResult.Success(outputs, trace: trace);
        }

        private static string Format(double value)
        {
            return value.ToString("0.###############", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}