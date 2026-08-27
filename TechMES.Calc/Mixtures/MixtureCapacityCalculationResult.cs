namespace TechMES.Calc.Mixtures;

/// <summary>
/// Результат Specific Heat Capacity для одного фактически участвующего компонента смеси.
///
/// Index соответствует исходному componentN/PercN слоту.
/// SpecificHeatCapacityJPerKgK - чистая теплоёмкость компонента в нормализованных единицах J/(kg·K), реально использованная при смешении.
/// </summary>
public sealed record MixtureCapacityComponentResult(int Index, string SubstanceCode, double MassPercent, double SpecificHeatCapacityJPerKgK);

/// <summary>
/// Полный результат Capacity смеси.
///
/// SpecificHeatCapacityJPerKgK - итоговая теплоёмкость смеси до DeltaC. Components - фактические Cp участвующих компонентов.
/// </summary>
public sealed record MixtureCapacityCalculationResult(double SpecificHeatCapacityJPerKgK, IReadOnlyList<MixtureCapacityComponentResult> Components);