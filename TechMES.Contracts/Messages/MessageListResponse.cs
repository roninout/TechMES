namespace TechMES.Contracts.Messages;

/// <summary>
/// Ответ API со списком сообщений.
///
/// Помимо самих сообщений мы сразу возвращаем количество активных сообщений.
/// Это удобно для будущего badge/индикатора в меню или header приложения.
/// </summary>
public sealed class MessageListResponse
{
    public IReadOnlyList<EquipmentMessageDto> Messages { get; set; } = [];

    public int ActiveMessageCount { get; set; }
}
