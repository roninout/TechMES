using System.Globalization;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Mixtures;

/// <summary>
/// Общая база для расчётов физических свойств многокомпонентной смеси.
///
/// Сейчас её используют Density и Capacity.
/// В дальнейшем эту же инфраструктуру можно использовать для других
/// расчётов свойств смеси, если у них будет аналогичная конфигурация компонентов.
///
/// Важно разделять два разных понятия:
///
/// 1. Физические входные параметры расчёта.
///    Например:
///    - Temperature;
///    - Pressure;
///    - в будущем Humidity, Concentration, Compressibility и т.д.
///
///    Их количество здесь НЕ ограничено.
///    Конкретный CalculationDefinition передаёт обычный список
///    CalculationParameterDefinition любой длины.
///
/// 2. Компоненты смеси.
///    Для них сейчас намеренно сохраняется максимум 5 компонентов,
///    потому что текущая структура Equipment в Plant SCADA содержит:
///
///    COMP_N
///    PERC_0
///    PERC_1
///    PERC_2
///    PERC_3
///    PERC_4
///
///    Это ограничение текущего SCADA-контракта смеси,
///    а не ограничение Calculation Engine.
/// </summary>
public abstract class MixtureCalculationDefinitionBase : CalculationDefinitionBase
{
    protected const int MaxComponentCount = 5;
    protected const string ComponentCountKey = "componentCount";

    /// <summary>
    /// Создаёт полный набор параметров Calculation Definition.
    ///
    /// propertyParameters содержит параметры конкретного физического свойства.
    ///
    /// Для Density сегодня это, например:
    /// - temperatureC;
    /// - pressureBarAbsolute;
    /// - densityCorrection.
    ///
    /// Для Capacity:
    /// - temperatureC;
    /// - pressureBarAbsolute;
    /// - capacityCorrection.
    ///
    /// Метод принципиально принимает IReadOnlyList, а не фиксированный набор полей.
    /// Поэтому для будущего алгоритма можно добавить третий, четвёртый,
    /// пятый или любое другое количество параметров без изменения этого класса,
    /// CalcJob, PostgreSQL или Calc.Service.
    ///
    /// После физических параметров автоматически добавляется конфигурация смеси:
    /// componentCount и пять возможных componentNCode/componentNPercent.
    /// </summary>
    protected static IReadOnlyList<CalculationParameterDefinition> CreateMixtureParameters(IReadOnlyList<CalculationParameterDefinition> propertyParameters)
    {
        ArgumentNullException.ThrowIfNull(propertyParameters);

        var result = new List<CalculationParameterDefinition>(propertyParameters.Count + 11);
        result.AddRange(propertyParameters);

        result.Add(new CalculationParameterDefinition(
            Key: ComponentCountKey,
            Name: "Component count",
            Type: CalculationParameterType.Integer,
            IsRequired: true,
            Minimum: 1,
            Maximum: MaxComponentCount,
            Step: 1,
            Decimals: 0,
            Order: 100,
            Description: "Number of active mixture components. Current SCADA structure supports from 1 to 5 components."));

        /*
         * Коды веществ не читаются непосредственно из SCADA.
         * В старом TechParamsCalc они хранились в PostgreSQL как perc0..perc4.
         *
         * В новой архитектуре это обычные Constant inputs Calc Job.
         * Поэтому пользователь сможет выбирать вещество из каталога,
         * а выбранный стабильный code будет храниться вместе с Job.
         */
        var substanceOptions = SubstanceCatalog.Items.Select(item => new CalculationParameterOption(item.Code, $"{item.Code} — {item.Name} ({GetPhaseName(item.Phase)})")).ToArray();

        /*
         * Внутренние ключи оставляем 0-based:
         *
         * component0Percent -> PERC_0
         * component1Percent -> PERC_1
         * ...
         * component4Percent -> PERC_4
         *
         * Это значительно упростит последующий автоматический binding
         * к ITEM существующего Plant SCADA Equipment Type.
         *
         * В пользовательском Name при этом показываем привычные номера 1..5.
         */
        for (var index = 0; index < MaxComponentCount; index++)
        {
            var displayIndex = index + 1;

            result.Add(new CalculationParameterDefinition(
                Key: GetComponentCodeKey(index),
                Name: $"Component {displayIndex}",
                Type: CalculationParameterType.Selection,
                IsRequired: false,
                Order: 110 + index * 10,
                Description: $"Substance used as mixture component {displayIndex}.",
                Options: substanceOptions));

            result.Add(new CalculationParameterDefinition(
                Key: GetComponentPercentKey(index),
                Name: $"Component {displayIndex} mass percent",
                Type: CalculationParameterType.Number,
                Unit: "%",
                IsRequired: false,
                Minimum: 0,
                Maximum: 100,
                Step: 0.1,
                Decimals: 3,
                Order: 111 + index * 10,
                Description: $"Mass percentage of mixture component {displayIndex}."));
        }

        return result;
    }

    /// <summary>
    /// Создаёт фактический список компонентов смеси из CalculationParameterSet.
    ///
    /// componentCount определяет, сколько первых componentNCode/componentNPercent
    /// действительно участвуют в расчёте.
    ///
    /// Остальные слоты не используются. Это позволяет одному Definition
    /// работать со смесями от одного до пяти компонентов.
    ///
    /// Хотя componentNCode и componentNPercent объявлены как IsRequired=false,
    /// для активных компонентов они становятся обязательными здесь.
    ///
    /// Такая условная обязательность не может быть выражена простым
    /// CalculationParameterDefinition.IsRequired, потому что она зависит
    /// от фактического значения componentCount.
    /// </summary>
    protected static IReadOnlyList<MixtureComponent> ReadMixtureComponents(CalculationParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var componentCount = parameters.GetRequiredInt(ComponentCountKey);

        if (componentCount < 1 || componentCount > MaxComponentCount)
        {
            throw new CalculationException(
                "mixture.component-count.invalid",
                $"Mixture component count must be between 1 and {MaxComponentCount}.");
        }

        var components = new List<MixtureComponent>(componentCount);

        for (var index = 0; index < componentCount; index++)
        {
            var codeKey = GetComponentCodeKey(index);
            var percentKey = GetComponentPercentKey(index);

            if (!parameters.TryGetValue(codeKey, out var rawCode) || rawCode is null)
            {
                throw new CalculationException(
                    "mixture.component.code-missing",
                    $"Substance code for mixture component {index + 1} is missing.");
            }

            if (!parameters.TryGetValue(percentKey, out var rawPercent) || rawPercent is null)
            {
                throw new CalculationException(
                    "mixture.component.percent-missing",
                    $"Mass percentage for mixture component {index + 1} is missing.");
            }

            var code = parameters.GetRequiredString(codeKey);
            var percent = parameters.GetRequiredDouble(percentKey);

            components.Add(new MixtureComponent(code, percent));
        }

        return components;
    }

    /// <summary>
    /// Добавляет в Trace полную фактическую конфигурацию смеси.
    ///
    /// Это особенно важно для последующей диагностики:
    /// по одному результату Density/Capacity будет видно,
    /// какие именно вещества и массовые доли участвовали в расчёте.
    /// </summary>
    protected static void AddMixtureTrace(ICollection<CalculationTraceItem> trace, IReadOnlyList<MixtureComponent> components)
    {
        trace.Add(new CalculationTraceItem(
            "componentCount",
            "Component count",
            components.Count.ToString(CultureInfo.InvariantCulture)));

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            trace.Add(new CalculationTraceItem(
                $"component{index}Code",
                $"Component {index + 1}",
                component.SubstanceCode));

            trace.Add(new CalculationTraceItem(
                $"component{index}Percent",
                $"Component {index + 1} mass percent",
                Format(component.MassPercent),
                "%"));
        }
    }

    /// <summary>
    /// Возвращает ключ выбора вещества для конкретного SCADA PERC index.
    /// </summary>
    protected static string GetComponentCodeKey(int index)
    {
        return $"component{index}Code";
    }

    /// <summary>
    /// Возвращает ключ массовой доли для конкретного SCADA PERC index.
    /// </summary>
    protected static string GetComponentPercentKey(int index)
    {
        return $"component{index}Percent";
    }

    /// <summary>
    /// Унифицированное форматирование double для Trace.
    ///
    /// Используем InvariantCulture, чтобы диагностическое значение
    /// не зависело от региональных настроек Windows.
    /// </summary>
    protected static string Format(double value)
    {
        return value.ToString("0.############", CultureInfo.InvariantCulture);
    }

    private static string GetPhaseName(SubstancePhase phase)
    {
        return phase switch
        {
            SubstancePhase.Liquid => "liquid",
            SubstancePhase.Vapor => "vapor",
            _ => phase.ToString()
        };
    }
}