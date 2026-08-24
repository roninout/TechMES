using TechMES.Calc.Exceptions;

namespace TechMES.Calc.Substances;

/// <summary>
/// Calculates physical properties of a mixture from mass percentages.
///
/// Unlike TechDotNetLib.Mix this class returns engineering values and does
/// not apply SCADA raw scaling (*10 for density or hidden short conversions).
/// </summary>
public static class MixturePropertyCalculator
{
    private const double PercentageTolerance = 1e-6;

    private static readonly HashSet<string> DensityModelsRequiringNormalization =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Freezium",
            "Methan",
            "Fusel"
        };

    private static readonly HashSet<string> CapacityModelsRequiringNormalization =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Methan"
        };

    /// <summary>
    /// Mixture density in kg/m³ using the same ideal-volume-additivity rule
    /// that was used by TechDotNetLib:
    /// rho = 1 / sum(w_i / rho_i).
    /// </summary>
    public static double CalculateDensityKgPerM3(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        double pressureBarAbsolute)
    {
        ValidateInputs(components, temperatureC, pressureBarAbsolute);

        var denominator = 0d;

        foreach (var component in components)
        {
            if (DensityModelsRequiringNormalization.Contains(component.SubstanceCode))
            {
                throw new CalculationException(
                    "substance.density.units-not-normalized",
                    $"Density model '{component.SubstanceCode}' still uses a legacy native unit contract and is not enabled for normalized Density calculation.");
            }

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureDensity = model.GetDensity((float)temperatureC, (float)pressureBarAbsolute);

            if (!double.IsFinite(pureDensity) || pureDensity <= 0)
            {
                throw new CalculationException(
                    "substance.density.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid density {pureDensity}.");
            }

            denominator += component.MassPercent * 0.01d / pureDensity;
        }

        if (!double.IsFinite(denominator) || denominator <= 0)
        {
            throw new CalculationException(
                "mixture.density.invalid-denominator",
                "Mixture density denominator must be greater than zero.");
        }

        var density = 1d / denominator;

        if (!double.IsFinite(density) || density <= 0)
        {
            throw new CalculationException(
                "mixture.density.invalid-result",
                "Calculated mixture density is invalid.");
        }

        return density;
    }

    /// <summary>
    /// Mixture specific heat capacity in J/(kg·K).
    /// Pure legacy models return kJ/(kg·K), therefore the weighted result is
    /// explicitly converted to J/(kg·K) here.
    /// </summary>
    public static double CalculateSpecificHeatCapacityJPerKgK(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC)
    {
        ValidateInputs(components, temperatureC, pressureBarAbsolute: 1d);

        var capacityKjPerKgK = 0d;

        foreach (var component in components)
        {
            if (CapacityModelsRequiringNormalization.Contains(component.SubstanceCode))
            {
                throw new CalculationException(
                    "substance.capacity.units-not-normalized",
                    $"Heat-capacity model '{component.SubstanceCode}' still uses a legacy native temperature/unit contract and is not enabled for normalized Capacity calculation.");
            }

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureCapacity = model.GetCapacity((float)temperatureC);

            if (!double.IsFinite(pureCapacity) || pureCapacity <= 0)
            {
                throw new CalculationException(
                    "substance.capacity.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid heat capacity {pureCapacity}.");
            }

            capacityKjPerKgK += component.MassPercent * 0.01d * pureCapacity;
        }

        var result = capacityKjPerKgK * 1000d;

        if (!double.IsFinite(result) || result < 0)
        {
            throw new CalculationException(
                "mixture.capacity.invalid-result",
                "Calculated mixture heat capacity is invalid.");
        }

        return result;
    }

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

        if (!double.IsFinite(pressureBarAbsolute) || pressureBarAbsolute < 0)
        {
            throw new CalculationException(
                "mixture.pressure.invalid",
                "Absolute pressure must be a finite non-negative number.");
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

            if (!usedCodes.Add(component.SubstanceCode.Trim()))
            {
                throw new CalculationException(
                    "mixture.component.duplicate",
                    $"Substance '{component.SubstanceCode}' is specified more than once.");
            }

            SubstanceCatalog.GetRequired(component.SubstanceCode);

            if (!double.IsFinite(component.MassPercent)
                || component.MassPercent < 0
                || component.MassPercent > 100)
            {
                throw new CalculationException(
                    "mixture.component.percent-invalid",
                    $"Mass percent for '{component.SubstanceCode}' must be between 0 and 100.");
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
