namespace TechMES.Calc.Content;

/// <summary>
/// Контракт вещества, которое содержит собственную основную
/// Content-корреляцию.
///
/// Не каждое вещество обязано реализовывать этот интерфейс.
/// Например Water в системе ACN + Water собственной формулы не имеет:
///
///     Water = 100% - ACN.
///
/// Поэтому Content не является обязательным методом LegacySubstance.
///
/// Метод всегда возвращает инженерное значение содержания самого
/// компонента в процентах 0..100, а не старый SCADA scale 0..10000.
/// </summary>
internal interface IContentSubstanceModel
{
    double GetContent(float temperature, float pressureBarAbsolute, ContentSystem system, int configurationCode);
}