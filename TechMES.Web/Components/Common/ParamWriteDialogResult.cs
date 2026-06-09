namespace TechMES.Web.Components.Common;

/// <summary>
/// Результат диалога записи Param item.
/// Диалог возвращает только новое значение, а комментарий audit parent-компонент формирует автоматически.
/// </summary>
public sealed class ParamWriteDialogResult
{
    /// <summary>
    /// Новое значение в строковом виде, готовое для ParamWriteRequest.Value.
    /// </summary>
    public string Value { get; init; } = "";
}
