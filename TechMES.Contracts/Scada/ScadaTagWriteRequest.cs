namespace TechMES.Contracts.Scada;

/// <summary>
/// Запрос на запись Plant SCADA tag.
/// 
/// WEB отправляет этот request в Runtime.Service.
/// Runtime.Service уже сам решает, можно ли писать,
/// есть ли права, доступен ли CtApi и как логировать действие.
/// </summary>
public sealed class ScadaTagWriteRequest
{
    public string TagName { get; set; } = "";

    public string? Value { get; set; }

    /// <summary>
    /// Кто инициировал запись.
    /// Пока можно не передавать — Runtime.Service подставит своё DeviceName/UserName.
    /// Позже будет реальный WEB-user.
    /// </summary>
    public string? Actor { get; set; }

    /// <summary>
    /// Комментарий/описание действия.
    /// Позже пригодится для operator action log.
    /// </summary>
    public string? Comment { get; set; }
}