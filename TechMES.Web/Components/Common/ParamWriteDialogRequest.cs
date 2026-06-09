using TechMES.Contracts.Param;

namespace TechMES.Web.Components.Common;

/// <summary>
/// Запрос на открытие общего write-dialog для Param item.
/// Нужен для switch-строк: они передают не только item, но и желаемое новое boolean-значение.
/// </summary>
public sealed class ParamWriteDialogRequest
{
    /// <summary>
    /// Param item, который нужно записать.
    /// </summary>
    public ParamItemDto? Item { get; init; }

    /// <summary>
    /// Начальное значение switch в диалоге.
    /// Если null, диалог берет текущее значение из Item.
    /// </summary>
    public bool? InitialBooleanValue { get; init; }
}
