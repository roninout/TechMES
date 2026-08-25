using TechMES.Calc.Exceptions;

namespace TechMES.Calc.Substances;

/// <summary>
/// Выполняет расчёт физических свойств смеси по массовым долям компонентов.
///
/// Этот класс является уже нашей новой оболочкой над перенесёнными формулами TechDotNetLib.
///
/// Важное отличие от старого Mix:
/// - здесь нет SCADA scaling;
/// - Density возвращается непосредственно в kg/m³;
/// - Capacity возвращается непосредственно в J/(kg·K);
/// - ошибки старых моделей не маскируются значениями -1;
/// - модели с пока не подтверждёнными единицами измерения явно запрещены.
///
/// Класс ничего не знает о:
/// - CtApi;
/// - Plant SCADA Equipment;
/// - ITEM/TAG;
/// - PostgreSQL;
/// - Calc Job.
///
/// Его задача исключительно математическая.
/// </summary>
public static class MixturePropertyCalculator
{
    private const double PercentageTolerance = 1e-6;

    /// <summary>
    /// Legacy-модели, которые пока нельзя использовать для нормализованного расчёта Density.
    ///
    /// Freezium:
    /// старая формула возвращает величину порядка 1.0, то есть фактически использует
    /// другую единицу плотности, а не ожидаемые kg/m³.
    ///
    /// Methan:
    /// новая формула внутри старого проекта ожидает Pressure в Pa и Temperature в K,
    /// тогда как старый общий Mix передавал bar(abs) и °C.
    ///
    /// Fusel:
    /// имеет ту же проблему K/Pa.
    ///
    /// HCL и NaOH:
    /// в старом проекте фактически содержат скопированную реализацию Diesel.
    /// До отдельного восстановления правильных формул использовать их нельзя.
    /// </summary>
    private static readonly HashSet<string> DensityModelsBlockedUntilVerified = new(StringComparer.OrdinalIgnoreCase)
    {
        "Freezium",
        "Methan",
        "Fusel",
        "HCL",
        "HCLS",
        "NaOH",
        "NaOHS"
    };

    /// <summary>
    /// Legacy-модели, которые пока нельзя использовать для нормализованного расчёта Capacity.
    ///
    /// Methan использует собственный температурный контракт.
    /// Fusel вообще не имеет реализованного расчёта Capacity.
    ///
    /// Diesel/HCL/NaOH возвращают значения другого масштаба относительно общего
    /// legacy-контракта kJ/(kg·K), поэтому автоматическое умножение на 1000
    /// для них сейчас было бы ошибочным.
    /// </summary>
    private static readonly HashSet<string> CapacityModelsBlockedUntilVerified = new(StringComparer.OrdinalIgnoreCase)
    {
        "Methan",
        "Fusel",
        "Diesel",
        "HCL",
        "HCLS",
        "NaOH",
        "NaOHS"
    };

    /// <summary>
    /// Рассчитывает плотность смеси в kg/m³.
    ///
    /// Используется тот же закон идеальной аддитивности объёмов,
    /// который применялся в TechDotNetLib.Mix:
    ///
    ///     rho = 1 / Σ(w_i / rho_i)
    ///
    /// где:
    /// w_i   - массовая доля компонента от 0 до 1;
    /// rho_i - плотность чистого компонента в kg/m³.
    ///
    /// Старое умножение результата на 10 здесь принципиально отсутствует,
    /// потому что оно относилось к SCADA raw scaling, а не к физической формуле.
    /// </summary>
    public static double CalculateDensityKgPerM3(IReadOnlyList<MixtureComponent> components, double temperatureC, double pressureBarAbsolute)
    {
        ValidateInputs(components, temperatureC, pressureBarAbsolute);

        var denominator = 0d;

        foreach (var component in components)
        {
            // Компонент с нулевой массовой долей физически не влияет на смесь.
            // Его код уже был проверен в ValidateInputs, поэтому здесь просто пропускаем расчёт модели.
            if (component.MassPercent == 0d)
                continue;

            EnsureDensityModelCanBeUsed(component.SubstanceCode);

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureDensity = model.GetDensity((float)temperatureC, (float)pressureBarAbsolute);

            if (!double.IsFinite(pureDensity) || pureDensity <= 0d)
            {
                throw new CalculationException(
                    "substance.density.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid density {pureDensity}.");
            }

            denominator += component.MassPercent * 0.01d / pureDensity;
        }

        if (!double.IsFinite(denominator) || denominator <= 0d)
            throw new CalculationException("mixture.density.invalid-denominator", "Mixture density denominator must be greater than zero.");

        var density = 1d / denominator;

        if (!double.IsFinite(density) || density <= 0d)
            throw new CalculationException("mixture.density.invalid-result", "Calculated mixture density is invalid.");

        return density;
    }

    /// <summary>
    /// Рассчитывает удельную теплоёмкость смеси в J/(kg·K).
    ///
    /// Для обычных перенесённых legacy-моделей GetCapacity() возвращает kJ/(kg·K).
    /// Теплоёмкость смеси рассчитывается как массово-взвешенная сумма:
    ///
    ///     Cp = Σ(w_i × Cp_i)
    ///
    /// После расчёта значение явно переводится из kJ/(kg·K) в J/(kg·K).
    ///
    /// Такой перевод находится здесь намеренно, чтобы единица результата
    /// была частью нового контракта Calc, а не скрытым поведением старого Mix.
    /// </summary>
    public static double CalculateSpecificHeatCapacityJPerKgK(IReadOnlyList<MixtureComponent> components, double temperatureC)
    {
        // Capacity не зависит от Pressure в текущих legacy-моделях.
        // Для общей проверки входных данных передаём физически допустимое абсолютное давление 1 bar(abs).
        ValidateInputs(components, temperatureC, pressureBarAbsolute: 1d);

        var capacityKjPerKgK = 0d;

        foreach (var component in components)
        {
            if (component.MassPercent == 0d)
                continue;

            EnsureCapacityModelCanBeUsed(component.SubstanceCode);

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureCapacity = model.GetCapacity((float)temperatureC);

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
            throw new CalculationException("mixture.capacity.invalid-result", "Calculated mixture heat capacity is invalid.");

        return capacityJPerKgK;
    }

    /// <summary>
    /// Выполняет общую проверку смеси перед запуском конкретной физической формулы.
    ///
    /// Здесь проверяется только общий контракт:
    /// - смесь содержит хотя бы один компонент;
    /// - Temperature является конечным числом;
    /// - абсолютное Pressure больше нуля;
    /// - каждый SubstanceCode существует в нашем каталоге;
    /// - массовая доля каждого компонента находится в диапазоне 0..100%;
    /// - один и тот же SubstanceCode не указан дважды;
    /// - сумма массовых долей равна 100%.
    ///
    /// Проверка пригодности конкретной модели для Density или Capacity выполняется
    /// отдельно, потому что один и тот же Substance может иметь рабочую Density-модель
    /// и одновременно неподтверждённую Capacity-модель.
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
            // Благодаря этому сохранённая конфигурация не сможет содержать неизвестный SubstanceCode.
            SubstanceCatalog.GetRequired(code);

            if (!double.IsFinite(component.MassPercent) || component.MassPercent < 0d || component.MassPercent > 100d)
                throw new CalculationException("mixture.component.percent-invalid", $"Mass percent for '{code}' must be between 0 and 100.");

            totalPercent += component.MassPercent;
        }

        if (Math.Abs(totalPercent - 100d) > PercentageTolerance)
            throw new CalculationException("mixture.percent-total-invalid", $"Mixture mass percentages must total 100%. Actual total: {totalPercent:0.######}%.");
    }

    /// <summary>
    /// Не позволяет использовать Density-модель, если мы уже знаем,
    /// что её единицы или сама формула требуют отдельной проверки.
    ///
    /// Это лучше, чем получить математически корректное double-значение,
    /// которое физически находится в другой системе единиц.
    /// </summary>
    private static void EnsureDensityModelCanBeUsed(string substanceCode)
    {
        if (DensityModelsBlockedUntilVerified.Contains(substanceCode))
        {
            throw new CalculationException(
                "substance.density.units-not-normalized",
                $"Density model '{substanceCode}' still uses an unverified legacy unit/formula contract and is not enabled for normalized Density calculation.");
        }
    }

    /// <summary>
    /// Не позволяет использовать Capacity-модель с неподтверждённым масштабом
    /// или температурным контрактом.
    /// </summary>
    private static void EnsureCapacityModelCanBeUsed(string substanceCode)
    {
        if (CapacityModelsBlockedUntilVerified.Contains(substanceCode))
        {
            throw new CalculationException(
                "substance.capacity.units-not-normalized",
                $"Heat-capacity model '{substanceCode}' still uses an unverified legacy unit/formula contract and is not enabled for normalized Capacity calculation.");
        }
    }
}