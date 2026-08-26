namespace TechMES.Calc.Mixtures;

/// <summary>
/// Результат Density для одного компонента смеси.
///
/// Index соответствует исходному componentN/PercN слоту.
/// DensityKgPerM3 - именно та плотность компонента, которая реально
/// использовалась MixturePropertyCalculator в текущем расчёте.
///
/// Для обычных веществ это плотность чистого вещества.
///
/// Для DryMatter это эффективная плотность при текущем MassPercent,
/// которая обеспечивает точное совпадение с исходной ICUMSA-корреляцией.
/// </summary>
public sealed record MixtureDensityComponentResult(int Index, string SubstanceCode, double MassPercent, double DensityKgPerM3);

/// <summary>
/// Полный результат расчёта Density смеси.
///
/// DensityKgPerM3 - итоговая плотность смеси до DeltaD.
/// Components - фактические плотности компонентов, участвовавших в расчёте.
/// </summary>
public sealed record MixtureDensityCalculationResult(double DensityKgPerM3, IReadOnlyList<MixtureDensityComponentResult> Components);