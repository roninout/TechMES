using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Mixtures;

/// <summary>
/// Выполняет расчёт физических свойств смеси по массовым долям компонентов.
///
/// Формулы отдельных веществ находятся в отдельных файлах
/// TechMES.Calc/Substances/Components и перенесены из TechDotNetLib.
///
/// Этот класс отвечает только за формулу смеси и единицы нового Calc-контракта:
/// - Density возвращается в kg/m³ без старого SCADA scaling ×10;
/// - Capacity возвращается в J/(kg·K);
/// - дополнительные ProcessInput передаются компонентам без изменения
///   старых GetDensity/GetCapacity/GetContent.
/// </summary>
public static class MixturePropertyCalculator
{
    private const double PercentageTolerance = 1e-6;

    /// <summary>
    /// Рассчитывает только итоговую плотность смеси в kg/m³.
    ///
    /// Метод оставляем как короткий совместимый API для существующего кода и тестов.
    /// Полный расчёт выполняет CalculateDensity(), который дополнительно возвращает
    /// фактическую Density каждого компонента.
    /// </summary>
    public static double CalculateDensityKgPerM3(IReadOnlyList<MixtureComponent> components, double temperatureC, double pressureBarAbsolute, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        return CalculateDensity(components, temperatureC, pressureBarAbsolute, additionalParameters).DensityKgPerM3;
    }

    /// <summary>
    /// Рассчитывает плотность смеси и возвращает фактические промежуточные
    /// плотности всех компонентов.
    ///
    /// Основная формула полностью соответствует старому TechDotNetLib.Mix:
    ///
    ///     rho = 1 / Σ(w_i / rho_i)
    ///
    /// где:
    /// w_i   - массовая доля компонента от 0 до 1;
    /// rho_i - плотность компонента в kg/m³.
    ///
    /// Для DryMatter rho_i является эффективной плотностью при текущем
    /// MassPercent. Это позволяет общей формуле смеси точно воспроизводить
    /// старую нелинейную ICUMSA-корреляцию.
    ///
    /// Старое финальное ×10 здесь отсутствует.
    /// Оно относится только к SCADA ValCalc и применяется Runtime при TagWrite.
    /// </summary>
    public static MixtureDensityCalculationResult CalculateDensity(IReadOnlyList<MixtureComponent> components, double temperatureC, double pressureBarAbsolute, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        ValidateInputs(components, temperatureC, pressureBarAbsolute);

        var denominator = 0d;
        var componentResults = new List<MixtureDensityComponentResult>();

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            // Компонент с нулевой массовой долей не участвует в формуле смеси.
            //
            // Его GetDensity намеренно не вызываем:
            // неактивный компонент не должен приводить расчёт к ошибке только потому,
            // что его физическая модель в текущей точке не поддерживается.
            if (component.MassPercent == 0d)
                continue;

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);

            // Передаём компоненту его собственную массовую долю.
            //
            // Все обычные legacy-компоненты игнорируют MassPercent и переходят
            // в исходный GetDensity(T, P).
            //
            // DryMatter использует MassPercent для восстановления старой
            // концентрационно-зависимой ICUMSA-корреляции.
            var componentDensity = model.GetDensity((float)temperatureC, (float)pressureBarAbsolute, component.MassPercent, additionalParameters);

            if (!double.IsFinite(componentDensity) || componentDensity <= 0d)
                throw new CalculationException("substance.density.invalid", $"Substance '{component.SubstanceCode}' returned invalid density {componentDensity}.");

            denominator += component.MassPercent * 0.01d / componentDensity;

            componentResults.Add(new MixtureDensityComponentResult(
                Index: index,
                SubstanceCode: component.SubstanceCode,
                MassPercent: component.MassPercent,
                DensityKgPerM3: componentDensity));
        }

        if (!double.IsFinite(denominator) || denominator <= 0d)
            throw new CalculationException("mixture.density.invalid-denominator", "Mixture density denominator must be greater than zero.");

        var density = 1d / denominator;

        if (!double.IsFinite(density) || density <= 0d)
            throw new CalculationException("mixture.density.invalid-result", "Calculated mixture density is invalid.");

        return new MixtureDensityCalculationResult(density, componentResults);
    }

    /// <summary>
    /// Рассчитывает удельную теплоёмкость смеси в J/(kg·K).
    ///
    /// Формула соответствует старому TechDotNetLib.Mix:
    ///
    ///     Cp = Σ(w_i × Cp_i)
    ///
    /// Старые GetCapacity() возвращают kJ/(kg·K),
    /// поэтому после смешения выполняется исходное ×1000.
    ///
    /// additionalParameters предназначены для будущих компонентов,
    /// которым стандартного Temperature недостаточно.
    /// </summary>
    public static double CalculateSpecificHeatCapacityJPerKgK(IReadOnlyList<MixtureComponent> components, double temperatureC, IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        // Capacity в исходном Mix не использовал Pressure. Для общей валидации передаём допустимое абсолютное давление.
        ValidateInputs(components, temperatureC, pressureBarAbsolute: 1d);

        var capacityKjPerKgK = 0d;

        foreach (var component in components)
        {
            if (component.MassPercent == 0d)
                continue;

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureCapacity = model.GetCapacity((float)temperatureC, additionalParameters);

            if (!double.IsFinite(pureCapacity) || pureCapacity <= 0d)
                throw new CalculationException("substance.capacity.invalid", $"Substance '{component.SubstanceCode}' returned invalid heat capacity {pureCapacity}.");

            capacityKjPerKgK += component.MassPercent * 0.01d * pureCapacity;
        }

        var capacityJPerKgK = capacityKjPerKgK * 1000d;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
            throw new CalculationException("substance.capacity.invalid-result", "Calculated mixture heat capacity is invalid.");

        return capacityJPerKgK;
    }

    /// <summary>
    /// Выполняет общую проверку смеси перед запуском конкретной физической формулы.
    ///
    /// Здесь намеренно нет списка "разрешённых" или "запрещённых" legacy-моделей:
    /// если формула присутствовала в TechDotNetLib, она остаётся доступной.
    ///
    /// Отдельная формула компонента сама определяет своё поведение.
    /// Некорректный конечный результат по-прежнему не пропускается дальше.
    /// </summary>
    private static void ValidateInputs(IReadOnlyList<MixtureComponent> components, double temperatureC, double pressureBarAbsolute)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0)
            throw new CalculationException("mixture.components.empty", "At least one mixture component is required.");

        if (!double.IsFinite(temperatureC))
            throw new CalculationException("mixture.temperature.invalid", "Mixture temperature must be a finite number.");

        if (!double.IsFinite(pressureBarAbsolute) || pressureBarAbsolute <= 0d)
            throw new CalculationException("mixture.pressure.invalid", "Absolute pressure must be a finite number greater than zero.");

        var totalPercent = 0d;
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.SubstanceCode))
                throw new CalculationException("mixture.component.code-empty", "Mixture component substance code cannot be empty.");

            var code = component.SubstanceCode.Trim();

            if (!usedCodes.Add(code))
                throw new CalculationException("mixture.component.duplicate", $"Substance '{code}' is specified more than once.");

            // Проверяем существование кода даже при MassPercent = 0.
            SubstanceCatalog.GetRequired(code);

            if (!double.IsFinite(component.MassPercent) || component.MassPercent < 0d || component.MassPercent > 100d)
                throw new CalculationException("mixture.component.percent-invalid", $"Mass percent for '{code}' must be between 0 and 100.");

            totalPercent += component.MassPercent;
        }

        if (Math.Abs(totalPercent - 100d) > PercentageTolerance)
            throw new CalculationException("mixture.percent-total-invalid", $"Mixture mass percentages must total 100%. Actual total: {totalPercent:0.######}%.");

        ValidateDryMatterComposition(components);
    }

    /// <summary>
    /// Проверяет специальный контракт DryMatter.
    /// Корреляция DryMatter восстановлена из исходного PLC-расчёта сахарного водного раствора.
    ///
    /// Поэтому допустимы только:
    ///
    ///     DryMatter = 100%
    ///
    /// либо:
    ///
    ///     Water + DryMatter = 100%.
    ///
    /// Использование DryMatter вместе с ACN, Alcohol и другими веществами математически не соответствует исходной ICUMSA-корреляции
    /// и должно завершаться явной ошибкой, а не давать внешне правдоподобное, но физически неверное значение.
    /// </summary>
    private static void ValidateDryMatterComposition(IReadOnlyList<MixtureComponent> components)
    {
        var activeComponents = components.Where(component => component.MassPercent > 0d).ToArray();
        var dryMatter = activeComponents.FirstOrDefault(component => string.Equals(component.SubstanceCode, "DryMatter", StringComparison.OrdinalIgnoreCase));

        if (dryMatter is null)
            return;

        // Чистый DryMatter 100% является допустимой контрольной точкой.
        if (activeComponents.Length == 1 && string.Equals(activeComponents[0].SubstanceCode, "DryMatter", StringComparison.OrdinalIgnoreCase) && Math.Abs(activeComponents[0].MassPercent - 100d) <= PercentageTolerance)
            return;

        // Для раствора разрешены только два активных компонента: Water и DryMatter.
        if (activeComponents.Length != 2)
            throw new CalculationException("mixture.drymatter.unsupported-combination", "DryMatter density correlation supports only pure DryMatter or a Water + DryMatter mixture.");

        var hasWater = activeComponents.Any(component =>
            string.Equals(component.SubstanceCode, "Water", StringComparison.OrdinalIgnoreCase));

        var hasOnlySupportedComponents = activeComponents.All(component => string.Equals(component.SubstanceCode, "Water", StringComparison.OrdinalIgnoreCase) || string.Equals(component.SubstanceCode, "DryMatter", StringComparison.OrdinalIgnoreCase));

        if (!hasWater || !hasOnlySupportedComponents)
            throw new CalculationException("mixture.drymatter.unsupported-combination", "DryMatter density correlation supports only pure DryMatter or a Water + DryMatter mixture.");
    }
}
