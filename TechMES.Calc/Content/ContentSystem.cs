namespace TechMES.Calc.Content;

/// <summary>
/// Физическая система, для которой выполняется Content-корреляция.
///
/// Здесь описывается именно система веществ, а не порядок output slots.
/// Например ACN + Water и Water + ACN используют одну и ту же
/// физическую корреляцию AcnWater, но имеют разный порядок результатов.
/// </summary>
internal enum ContentSystem
{
    AcnWater = 1,
    PoPropylene = 2,
    PoWater = 3,
    AcaPo = 4,
    AlcWater = 5,
    AcnWaterPo = 6
}