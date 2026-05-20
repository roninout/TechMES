namespace TechMES.Contracts.Messages;

/// <summary>
/// Уведомление о том, что список сообщений изменился.
/// 
/// Runtime.Service отправляет этот объект через SignalR.
/// WEB получает его и перечитывает список сообщений через обычный HTTP API.
/// </summary>
public sealed class MessageChangedNotification
{
    /// <summary>
    /// Какое изменение произошло: создание, обновление, удаление и т.д.
    /// </summary>
    public MessageChangedEventType EventType { get; set; }

    /// <summary>
    /// Id сообщения, которого касается изменение.
    /// Для некоторых будущих массовых операций может быть null.
    /// </summary>
    public long? MessageId { get; set; }

    /// <summary>
    /// Кто выполнил действие.
    /// Сейчас это DeviceName, позже может быть имя пользователя.
    /// </summary>
    public string ChangedBy { get; set; } = "";

    /// <summary>
    /// Время события на стороне Runtime.Service.
    /// </summary>
    public DateTime ChangedAt { get; set; } = DateTime.Now;
}