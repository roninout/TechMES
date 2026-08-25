using TechMES.Calc.Exceptions;

namespace TechMES.Calc.Substances;

/// <summary>
/// Выполняет расчёт физических свойств смеси по массовым долям компонентов.
///
/// Формулы отдельных веществ находятся в отдельных файлах
/// TechMES.Calc/Substances/Legacy и перенесены из TechDotNetLib.
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
    /// Рассчитывает плотность смеси в kg/m³.
    ///
    /// Формула полностью соответствует старому TechDotNetLib.Mix:
    ///
    ///     rho = 1 / Σ(w_i / rho_i)
    ///
    /// где:
    /// w_i   - массовая доля компонента от 0 до 1;
    /// rho_i - плотность чистого компонента в kg/m³.
    ///
    /// Единственное намеренное отличие от старого Mix:
    /// старое финальное умножение ×10 здесь отсутствует,
    /// потому что это SCADA scaling, а не физическая формула.
    /// Scale=10 применяется Runtime непосредственно перед записью ValCalc.
    /// </summary>
    public static double CalculateDensityKgPerM3(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        double pressureBarAbsolute,
        IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        ValidateInputs(components, temperatureC, pressureBarAbsolute);

        var denominator = 0d;

        foreach (var component in components)
        {
            // Нулевая массовая доля не влияет на смесь.
            if (component.MassPercent == 0d)
                continue;

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);

            // Все старые вещества по умолчанию попадают в исходный
            // GetDensity(float temperature, float pressure).
            //
            // Если новый компонент переопределит расширенную перегрузку,
            // он дополнительно получит additionalParameters.
            var pureDensity = model.GetDensity(
                (float)temperatureC,
                (float)pressureBarAbsolute,
                additionalParameters);

            if (!double.IsFinite(pureDensity) || pureDensity <= 0d)
            {
                throw new CalculationException(
                    "substance.density.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid density {pureDensity}.");
            }

            denominator += component.MassPercent * 0.01d / pureDensity;
        }

        if (!double.IsFinite(denominator) || denominator <= 0d)
        {
            throw new CalculationException(
                "mixture.density.invalid-denominator",
                "Mixture density denominator must be greater than zero.");
        }

        var density = 1d / denominator;

        if (!double.IsFinite(density) || density <= 0d)
        {
            throw new CalculationException(
                "mixture.density.invalid-result",
                "Calculated mixture density is invalid.");
        }

        return density;
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
    public static double CalculateSpecificHeatCapacityJPerKgK(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        // Capacity в исходном Mix не использовал Pressure.
        // Для общей валидации передаём допустимое абсолютное давление.
        ValidateInputs(components, temperatureC, pressureBarAbsolute: 1d);

        var capacityKjPerKgK = 0d;

        foreach (var component in components)
        {
            if (component.MassPercent == 0d)
                continue;

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureCapacity = model.GetCapacity((float)temperatureC, additionalParameters);

            if (!double.IsFinite(pureCapacity) || pureCapacity <= 0d)
            {
                throw new CalculationException(
                    "substance.capacity.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid heat capacity {pureCapacity}.");
            }

            capacityKjPerKgK += component.MassPercent * 0.01d * pureCapacity;
        }

        var capacityJPerKgK = capacityKjPerKgK * 1000d;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
        {
            throw new CalculationException(
                "substance.capacity.invalid-result",
                "Calculated mixture heat capacity is invalid.");
        }

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
    private static void ValidateInputs(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        double pressureBarAbsolute)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0)
        {
            throw new CalculationException(
                "mixture.components.empty",
                "At least one mixture component is required.");
        }

        if (!double.IsFinite(temperatureC))
        {
            throw new CalculationException(
                "mixture.temperature.invalid",
                "Mixture temperature must be a finite number.");
        }

        if (!double.IsFinite(pressureBarAbsolute) || pressureBarAbsolute <= 0d)
        {
            throw new CalculationException(
                "mixture.pressure.invalid",
                "Absolute pressure must be a finite number greater than zero.");
        }

        var totalPercent = 0d;
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.SubstanceCode))
            {
                throw new CalculationException(
                    "mixture.component.code-empty",
                    "Mixture component substance code cannot be empty.");
            }

            var code = component.SubstanceCode.Trim();

            if (!usedCodes.Add(code))
            {
                throw new CalculationException(
                    "mixture.component.duplicate",
                    $"Substance '{code}' is specified more than once.");
            }

            // Проверяем существование кода даже при MassPercent = 0.
            SubstanceCatalog.GetRequired(code);

            if (!double.IsFinite(component.MassPercent)
                || component.MassPercent < 0d
                || component.MassPercent > 100d)
            {
                throw new CalculationException(
                    "mixture.component.percent-invalid",
                    $"Mass percent for '{code}' must be between 0 and 100.");
            }

            totalPercent += component.MassPercent;
        }

        if (Math.Abs(totalPercent - 100d) > PercentageTolerance)
        {
            throw new CalculationException(
                "mixture.percent-total-invalid",
                $"Mixture mass percentages must total 100%. Actual total: {totalPercent:0.######}%.");
        }
    }
}
