using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Content;

/// <summary>
/// Комбинатор Content-систем.
///
/// Здесь нет физических коэффициентов и полиномов.
///
/// Его ответственность:
/// - определить физическую систему по упорядоченному набору компонентов;
/// - найти компонент с основной Content-корреляцией;
/// - вычислить зависимые компоненты через материальный баланс;
/// - вернуть результаты в том же порядке, в котором компоненты заданы в Job.
/// </summary>
internal static class ContentCombinationCalculator
{
    private static readonly IReadOnlyDictionary<string, ContentCombinationDefinition> Definitions = CreateDefinitions();

    public static bool TryCalculatePercent(IReadOnlyList<string> components, float temperature, float pressureBarAbsolute, int configurationCode, out IReadOnlyList<double> result)
    {
        var key = BuildKey(components);

        if (!Definitions.TryGetValue(key, out var definition))
        {
            result = [];
            return false;
        }

        result = definition.Kind switch
        {
            ContentCombinationKind.BinaryComplement => CalculateBinaryComplement(definition, components, temperature, pressureBarAbsolute, configurationCode),
            _ => throw new CalculationException("content.combination.unsupported", $"Content combination kind '{definition.Kind}' is not implemented.")
        };

        return true;
    }

    /// <summary>
    /// Расчёт бинарной смеси.
    ///
    /// Корреляция рассчитывает содержание основного компонента,
    /// содержание второго компонента определяется материальным балансом:
    ///
    /// Secondary = 100 - Primary.
    ///
    /// После этого значения переставляются в том порядке,
    /// в котором компоненты были указаны в Calculation Job.
    /// </summary>
    private static IReadOnlyList<double> CalculateBinaryComplement(ContentCombinationDefinition definition, IReadOnlyList<string> requestedOrder, float temperature,float pressureBarAbsolute, int configurationCode)
    {
        var model = SubstanceCatalog.CreateRequiredModel(definition.PrimaryComponentCode);

        if (model is not IContentSubstanceModel contentModel)
            throw new CalculationException("content.model.unsupported", $"Substance '{definition.PrimaryComponentCode}' does not provide Content correlation '{definition.System}'.");

        var primaryContent = contentModel.GetContent(temperature, pressureBarAbsolute, definition.System, configurationCode);
        var secondaryContent = 100.0 - primaryContent;
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [definition.PrimaryComponentCode] = primaryContent,
            [definition.SecondaryComponentCode] = secondaryContent
        };

        return requestedOrder.Select(code => values[code]).ToArray();
    }

    /// <summary>
    /// Список реально поддерживаемых комбинаций Content.
    ///
    /// Важно:
    /// ContentSystem описывает физическую систему.
    /// Порядок компонентов описывается ключами Definitions.
    ///
    /// Например:
    ///     PO + P
    ///     P + PO
    ///
    /// используют одну физическую корреляцию PoPropylene,
    /// но возвращают результаты в разном порядке.
    /// </summary>
    private static IReadOnlyDictionary<string, ContentCombinationDefinition>CreateDefinitions()
    {
        var result = new Dictionary<string, ContentCombinationDefinition>(StringComparer.OrdinalIgnoreCase);

        void AddBinary(ContentSystem system, string primaryComponentCode, string secondaryComponentCode, params string[][] supportedOrders)
        {
            var definition = new ContentCombinationDefinition(
                System: system,
                Kind: ContentCombinationKind.BinaryComplement,
                PrimaryComponentCode: primaryComponentCode,
                SecondaryComponentCode: secondaryComponentCode);

            foreach (var order in supportedOrders)
                result.Add(BuildKey(order), definition);
        }

        // ACN + Water
        //
        // Основная корреляция рассчитывает ACN.
        AddBinary(ContentSystem.AcnWater,
            "ACN",
            "Water",
            ["ACN", "Water"],
            ["Water", "ACN"]);

        // PO + Propylene
        //
        // Основная корреляция рассчитывает PO.
        AddBinary(ContentSystem.PoPropylene,
            "PO",
            "P",
            ["PO", "P"],
            ["P", "PO"]);

        // PO + Water
        //
        // Основная корреляция рассчитывает PO.
        AddBinary(ContentSystem.PoWater,
            "PO",
            "Water",
            ["PO", "Water"],
            ["Water", "PO"]);

        // Acetaldehyde + PO
        //
        // Основная корреляция рассчитывает ACA.
        AddBinary(ContentSystem.AcaPo,
            "ACA",
            "PO",
            ["ACA", "PO"],
            ["PO", "ACA"]);

        // Alcohol + Water
        //
        // В исходном ContentCalc существует только порядок ALC + Water.
        // Поэтому искусственно добавлять Water + ALC не нужно.
        AddBinary(ContentSystem.AlcWater,
            "ALC",
            "Water",
            ["ALC", "Water"]);

        return result;
    }

    private static string BuildKey(IEnumerable<string> components)
    {
        return string.Join("|", components.Select(component => component.Trim()));
    }

    private sealed record ContentCombinationDefinition(ContentSystem System, ContentCombinationKind Kind, string PrimaryComponentCode, string SecondaryComponentCode);

    private enum ContentCombinationKind
    {
        BinaryComplement = 1
    }
}