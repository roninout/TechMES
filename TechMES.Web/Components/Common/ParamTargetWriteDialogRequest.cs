using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Запрос на открытие write-dialog для Param item-а, который принадлежит не текущему
/// выбранному оборудованию, а связанному target equipment.
/// Используется DryRun и PLC reference page: UI выбранного оборудования показывает ссылку,
/// а запись физически выполняется в связанный tag-контекст.
/// </summary>
public sealed class ParamTargetWriteDialogRequest
{
    /// <summary>
    /// Имя оборудования, в которое нужно записать Param item.
    /// </summary>
    public string EquipmentName { get; init; } = "";

    /// <summary>
    /// Param item, для которого открывается общий write-dialog.
    /// </summary>
    public ParamItemDto? Item { get; init; }

    /// <summary>
    /// Начальное boolean-значение для switch-сценариев.
    /// Null означает обычный числовой или текстовый ввод.
    /// </summary>
    public bool? InitialBooleanValue { get; init; }

    /// <summary>
    /// Разрешает открыть write-dialog для item-а, который пришел из reference snapshot без CanWrite.
    /// Окончательное право записи всегда повторно проверяет Runtime.Service.
    /// </summary>
    public bool AllowReadOnlyItemDialog { get; init; }

    /// <summary>
    /// Имя оборудования, из PLC reference page которого пришла строка.
    /// Backend использует это имя для повторной проверки TabPLC перед reference-write.
    /// </summary>
    public string? ReferenceSourceEquipmentName { get; init; }

    /// <summary>
    /// Тип значения reference-write: boolean для switch/button, number для EqNumW.
    /// </summary>
    public ParamValueKind? ReferenceValueKind { get; init; }
}
