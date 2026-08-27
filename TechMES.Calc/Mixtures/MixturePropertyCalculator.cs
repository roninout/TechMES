using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Mixtures;

/// <summary>
/// Выполняет расчёт физических свойств смеси по массовым долям компонентов.
/// Формулы отдельных веществ находятся в отдельных файлах TechMES.Calc/Substances/Components и перенесены из TechDotNetLib.
///
/// Этот класс отвечает только за формулу смеси и единицы нового Calc-контракта:
/// - Density возвращается в kg/m³ без старого SCADA scaling ×10;
/// - Capacity возвращается в J/(kg·K);
/// - дополнительные ProcessInput передаются компонентам без изменения   старых GetDensity/GetCapacity/GetContent.
/// </summary>
public static class MixturePropertyCalculator
{
    private const double PercentageTolerance = 1e-6;

    public static double CalculateDensityKgPerM3(IReadOnlyList<MixtureComponent> components, double temperatureC, double pressureBarAbsolute, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        return CalculateDensity(components, temperatureC, pressureBarAbsolute,additionalParameters).DensityKgPerM3;
    }

    /// <summary>
    /// Рассчитывает Density смеси:
    ///
    ///     rho = 1 / Σ(w_i / rho_i)
    ///
    /// Density-specific проверки находятся именно здесь:
    /// - корректное абсолютное давление;
    /// - специальный контракт DryMatter / ICUMSA.
    ///
    /// Благодаря этому Capacity больше не наследует Density-only правила.
    /// </summary>
    public static MixtureDensityCalculationResult CalculateDensity(IReadOnlyList<MixtureComponent> components, double temperatureC, double pressureBarAbsolute, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        ValidateCommonInputs(components, temperatureC);
        ValidateAbsolutePressure(pressureBarAbsolute);
        ValidateDryMatterComposition(components);

        var denominator = 0d;
        var componentResults = new List<MixtureDensityComponentResult>();

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            if (component.MassPercent == 0d)
                continue;

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var componentDensity = model.GetDensity((float)temperatureC, (float)pressureBarAbsolute, component.MassPercent, additionalParameters);

            if (!double.IsFinite(componentDensity) || componentDensity <= 0d)
                throw new CalculationException("substance.density.invalid", $"Substance '{component.SubstanceCode}' returned invalid density {componentDensity}.");

            denominator += component.MassPercent * 0.01d / componentDensity;
            componentResults.Add(new MixtureDensityComponentResult(Index: index, SubstanceCode: component.SubstanceCode, MassPercent: component.MassPercent, DensityKgPerM3: componentDensity));
        }

        if (!double.IsFinite(denominator) || denominator <= 0d)
            throw new CalculationException("mixture.density.invalid-denominator", "Mixture density denominator must be greater than zero.");

        var density = 1d / denominator;

        if (!double.IsFinite(density) || density <= 0d)
            throw new CalculationException("mixture.density.invalid-result", "Calculated mixture density is invalid.");

        return new MixtureDensityCalculationResult(density, componentResults);
    }

    /// <summary>
    /// Короткий совместимый API, возвращающий только итоговую Capacity смеси.
    /// Полный вариант CalculateSpecificHeatCapacity дополнительно возвращает
    /// фактическую Cp каждого компонента.
    /// </summary>
    public static double CalculateSpecificHeatCapacityJPerKgK(IReadOnlyList<MixtureComponent> components, double temperatureC, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        return CalculateSpecificHeatCapacity(components, temperatureC, additionalParameters).SpecificHeatCapacityJPerKgK;
    }

    /// <summary>
    /// Рассчитывает удельную теплоёмкость смеси в J/(kg·K).
    ///
    /// Формула соответствует старому TechDotNetLib.Mix:
    ///
    ///     Cp = Σ(w_i × Cp_i)
    ///
    /// Legacy GetCapacity() возвращает kJ/(kg·K).
    /// В нормализованный TechMES contract каждый component Cp и итог смеси
    /// переводятся в J/(kg·K) через ×1000.
    ///
    /// В отличие от Density:
    /// - Pressure здесь не валидируется и не используется;
    /// - DryMatter/ICUMSA composition rule здесь не применяется;
    /// - поддержка Capacity проверяется отдельной capability metadata.
    /// </summary>
    public static MixtureCapacityCalculationResult CalculateSpecificHeatCapacity(IReadOnlyList<MixtureComponent> components, double temperatureC, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        ValidateCommonInputs(components, temperatureC);
        ValidateDryMatterComposition(components);

        var capacityKjPerKgK = 0d;
        var componentResults = new List<MixtureCapacityComponentResult>();

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            // Неактивный компонент не определяет возможность текущего расчёта. Это сохраняет то же поведение, которое уже используется Density.
            if (component.MassPercent == 0d)
                continue;

            var descriptor = SubstanceCatalog.GetRequired(component.SubstanceCode);

            if (!descriptor.Supports(SubstancePropertySupport.SpecificHeatCapacity))
                throw new CalculationException("substance.capacity.unsupported", $"Substance '{component.SubstanceCode}' is not supported by the normalized specific heat capacity calculation.");

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureCapacityKjPerKgK = model.GetCapacity((float)temperatureC, component.MassPercent, additionalParameters);

            if (!double.IsFinite(pureCapacityKjPerKgK) || pureCapacityKjPerKgK <= 0d)
                throw new CalculationException("substance.capacity.invalid", $"Substance '{component.SubstanceCode}' returned invalid heat capacity {pureCapacityKjPerKgK}.");

            capacityKjPerKgK += component.MassPercent * 0.01d * pureCapacityKjPerKgK;
            componentResults.Add(new MixtureCapacityComponentResult(Index: index, SubstanceCode: component.SubstanceCode, MassPercent: component.MassPercent, SpecificHeatCapacityJPerKgK: pureCapacityKjPerKgK * 1000d));
        }

        var capacityJPerKgK = capacityKjPerKgK * 1000d;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
            throw new CalculationException("substance.capacity.invalid-result", "Calculated mixture heat capacity is invalid.");

        return new MixtureCapacityCalculationResult(capacityJPerKgK, componentResults);
    }

    /// <summary>
    /// Общая для Density/Capacity структурная проверка смеси.
    ///
    /// Здесь остаются только действительно общие правила:
    /// - смесь не пустая;
    /// - Temperature конечна;
    /// - коды существуют и не повторяются;
    /// - проценты лежат в 0..100 и дают 100%;
    /// - активные компоненты принадлежат одной фазе.
    ///
    /// Property-specific правила выполняются конкретным расчётом отдельно.
    /// </summary>
    private static void ValidateCommonInputs(IReadOnlyList<MixtureComponent> components, double temperatureC)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0)
            throw new CalculationException("mixture.components.empty", "At least one mixture component is required.");

        if (!double.IsFinite(temperatureC))
            throw new CalculationException("mixture.temperature.invalid", "Mixture temperature must be a finite number.");

        var totalPercent = 0d;
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.SubstanceCode))
                throw new CalculationException("mixture.component.code-empty", "Mixture component substance code cannot be empty.");

            var code = component.SubstanceCode.Trim();

            if (!usedCodes.Add(code))
                throw new CalculationException("mixture.component.duplicate", $"Substance '{code}' is specified more than once.");

            // Существование кода проверяем даже для 0%.
            SubstanceCatalog.GetRequired(code);

            if (!double.IsFinite(component.MassPercent) || component.MassPercent < 0d || component.MassPercent > 100d)
                throw new CalculationException("mixture.component.percent-invalid", $"Mass percent for '{code}' must be between 0 and 100.");

            totalPercent += component.MassPercent;
        }

        if (Math.Abs(totalPercent - 100d) > PercentageTolerance)
            throw new CalculationException("mixture.percent-total-invalid", $"Mixture mass percentages must total 100%. Actual total: {totalPercent:0.######}%.");

        ValidateSinglePhaseComposition(components);
    }

    private static void ValidateAbsolutePressure(double pressureBarAbsolute)
    {
        if (!double.IsFinite(pressureBarAbsolute) || pressureBarAbsolute <= 0d)
            throw new CalculationException("mixture.pressure.invalid", "Absolute pressure must be a finite number greater than zero.");
    }

    private static void ValidateSinglePhaseComposition(IReadOnlyList<MixtureComponent> components)
    {
        SubstancePhase? mixturePhase = null;

        foreach (var component in components.Where(component => component.MassPercent > 0d))
        {
            var descriptor = SubstanceCatalog.GetRequired(component.SubstanceCode);

            if (!mixturePhase.HasValue)
            {
                mixturePhase = descriptor.Phase;
                continue;
            }

            if (descriptor.Phase != mixturePhase.Value)
                throw new CalculationException("mixture.phase-mixed", "Liquid and vapor components cannot be mixed in the same calculation.");
        }
    }

    /// <summary>
    /// Специальный контракт DryMatter.
    /// Density и Capacity DryMatter восстановлены из корреляций сахарного водного раствора.
    ///
    /// Поэтому поддерживаются только:
    /// - DryMatter = 100%;
    /// - Water + DryMatter = 100%.
    /// </summary>
    private static void ValidateDryMatterComposition(IReadOnlyList<MixtureComponent> components)
    {
        var activeComponents = components.Where(component => component.MassPercent > 0d).ToArray();
        var dryMatter = activeComponents.FirstOrDefault(component => string.Equals(component.SubstanceCode, "DryMatter", StringComparison.OrdinalIgnoreCase));

        if (dryMatter is null)
            return;

        if (activeComponents.Length == 1 && string.Equals(activeComponents[0].SubstanceCode, "DryMatter", StringComparison.OrdinalIgnoreCase) && Math.Abs(activeComponents[0].MassPercent - 100d) <= PercentageTolerance)
            return;

        if (activeComponents.Length != 2)
            throw new CalculationException("mixture.drymatter.unsupported-combination", "DryMatter density correlation supports only pure DryMatter or a Water + DryMatter mixture.");

        var hasWater = activeComponents.Any(component => string.Equals(component.SubstanceCode, "Water", StringComparison.OrdinalIgnoreCase));
        var hasOnlySupportedComponents = activeComponents.All(component => string.Equals(component.SubstanceCode, "Water", StringComparison.OrdinalIgnoreCase) || string.Equals(component.SubstanceCode, "DryMatter", StringComparison.OrdinalIgnoreCase));

        if (!hasWater || !hasOnlySupportedComponents)
            throw new CalculationException("mixture.drymatter.unsupported-combination", "DryMatter density correlation supports only pure DryMatter or a Water + DryMatter mixture.");
    }
}