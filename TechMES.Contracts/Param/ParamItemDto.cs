namespace TechMES.Contracts.Param;

/// <summary>
/// Значение одного Param item, которое Runtime.Service возвращает в WEB.
/// DTO не содержит CtApi-объектов: только готовые к отображению значения и служебные признаки.
/// </summary>
public sealed class ParamItemDto
{
    /// <summary>
    /// Имя EquipItem в Plant SCADA/CtApi, например MinR, ForceCmd или SetHyst.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Тип значения для UI: boolean отображается чекбоксом, number - числовым вводом.
    /// </summary>
    public ParamValueKind Kind { get; set; } = ParamValueKind.Unknown;

    /// <summary>
    /// Отформатированное значение для таблиц и карточек.
    /// </summary>
    public string? ValueText { get; set; }

    /// <summary>
    /// Числовое представление, если значение удалось разобрать как number.
    /// </summary>
    public double? NumericValue { get; set; }

    /// <summary>
    /// Boolean-представление, если значение удалось разобрать как on/off.
    /// </summary>
    public bool? BooleanValue { get; set; }

    /// <summary>
    /// Единица измерения тега, если CtApi/TagInfo смог ее вернуть.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Реальное имя тега Plant SCADA, разрешенное из Equipment + EquipItem.
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Признак, что item входит в наш WEB allow-list для записи.
    /// UI показывает кнопку редактирования только когда CanWrite = true.
    /// </summary>
    public bool CanWrite { get; set; }
}
