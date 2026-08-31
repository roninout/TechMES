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
/// - выбрать способ расчёта системы;
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
            ContentCombinationKind.MultiComponent => CalculateMultiComponent(definition, components, temperature, pressureBarAbsolute, configurationCode),

            _ => throw new CalculationException("content.combination.unsupported", $"Content combination kind '{definition.Kind}' is not implemented.")
        };

        return true;
    }

    /// <summary>
    /// Бинарная смесь:
    ///
    /// Secondary = 100 - Primary.
    /// </summary>
    private static IReadOnlyList<double> CalculateBinaryComplement(ContentCombinationDefinition definition, IReadOnlyList<string> requestedOrder, float temperature, float pressureBarAbsolute, int configurationCode)
    {
        if (definition.PrimaryComponentCode is null || definition.SecondaryComponentCode is null)
            throw new CalculationException("content.combination.invalid-definition", $"Binary Content system '{definition.System}' does not define primary and secondary components.");

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
    /// Многокомпонентная Content-система.
    ///
    /// В отличие от BinaryComplement здесь одна корреляция возвращает сразу содержания всех компонентов системы.
    /// </summary>
    private static IReadOnlyList<double> CalculateMultiComponent(ContentCombinationDefinition definition, IReadOnlyList<string> requestedOrder, float temperature, float pressureBarAbsolute, int configurationCode)
    {
        IReadOnlyDictionary<string, double> values = definition.System switch
        {
            ContentSystem.AcnWaterPo => AcnWaterPoContentModel.CalculatePercent(temperature, pressureBarAbsolute, configurationCode),

            _ => throw new CalculationException("content.system.unsupported", $"Multi-component Content correlation is not defined for system '{definition.System}'.")
        };

        return requestedOrder.Select(code => values[code]).ToArray();
    }

    private static IReadOnlyDictionary<string, ContentCombinationDefinition> CreateDefinitions()
    {
        var result = new Dictionary<string, ContentCombinationDefinition>(StringComparer.OrdinalIgnoreCase);

        void AddBinary(ContentSystem system, string primaryComponentCode, string secondaryComponentCode, params string[][] supportedOrders)
        {
            var definition = new ContentCombinationDefinition(System: system, Kind: ContentCombinationKind.BinaryComplement, PrimaryComponentCode: primaryComponentCode, SecondaryComponentCode: secondaryComponentCode);

            foreach (var order in supportedOrders)
                result.Add(BuildKey(order), definition);
        }

        void AddMultiComponent(ContentSystem system, params string[][] supportedOrders)
        {
            var definition = new ContentCombinationDefinition(System: system, Kind: ContentCombinationKind.MultiComponent, PrimaryComponentCode: null, SecondaryComponentCode: null);

            foreach (var order in supportedOrders)
                result.Add(BuildKey(order), definition);
        }

        AddBinary(ContentSystem.AcnWater, "ACN", "Water",
            ["ACN", "Water"],
            ["Water", "ACN"]);

        AddBinary(ContentSystem.PoPropylene, "PO", "P",
            ["PO", "P"],
            ["P", "PO"]);

        AddBinary(ContentSystem.PoWater, "PO", "Water",
            ["PO", "Water"],
            ["Water", "PO"]);

        AddBinary(ContentSystem.AcaPo, "ACA", "PO",
            ["ACA", "PO"],
            ["PO", "ACA"]);

        AddBinary(ContentSystem.AlcWater, "ALC", "Water",
            ["ALC", "Water"]);

        // В старом TechDotNetLib были реализованы именно эти два порядка. Остальные перестановки трёх компонентов не добавляем искусственно.
        AddMultiComponent(ContentSystem.AcnWaterPo,
            ["ACN", "Water", "PO"],
            ["PO", "Water", "ACN"]);

        return result;
    }

    private static string BuildKey(IEnumerable<string> components)
    {
        return string.Join("|", components.Select(component => component.Trim()));
    }

    private sealed record ContentCombinationDefinition(ContentSystem System, ContentCombinationKind Kind, string? PrimaryComponentCode, string? SecondaryComponentCode);

    private enum ContentCombinationKind
    {
        BinaryComplement = 1,
        MultiComponent = 2
    }
}