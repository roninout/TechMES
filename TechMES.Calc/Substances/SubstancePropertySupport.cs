namespace TechMES.Calc.Substances;

/// <summary>
/// Физические свойства, для которых конкретная legacy-модель вещества
/// разрешена в соответствующем TechMES Calculation Definition.
///
/// Наличие старого метода GetDensity/GetCapacity/GetContent само по себе
/// ещё не означает, что свойство должно быть доступно Production UI.
/// Например, формула может иметь старый нестандартный контракт единиц
/// либо возвращать legacy sentinel вместо реального значения.
/// </summary>
[Flags]
public enum SubstancePropertySupport
{
    None = 0,

    /// <summary>
    /// Density calculation.
    /// </summary>
    Density = 1 << 0,

    /// <summary>
    /// Specific heat capacity calculation.
    /// </summary>
    SpecificHeatCapacity = 1 << 1,

    /// <summary>
    /// Content calculation.
    /// Для Content фактическая поддержка дополнительно зависит
    /// от допустимой комбинации компонентов.
    /// </summary>
    Content = 1 << 2
}