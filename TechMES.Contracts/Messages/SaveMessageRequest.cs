namespace TechMES.Contracts.Messages;

/// <summary>
/// Запрос на создание или сохранение сообщения.
///
/// Почему это отдельный класс, а не EquipmentMessageDto:
/// WEB при сохранении должен отправлять только то, что пользователь может изменить.
/// Например CreatedBy/CreatedAt/View flags заполняются на стороне Runtime Service,
/// а не приходят из браузера.
/// </summary>
public sealed class SaveMessageRequest
{
    /// <summary>
    /// 0 означает новое сообщение.
    /// Если Id больше 0 — Runtime Service обновляет существующее сообщение.
    /// </summary>
    public long Id { get; set; }

    public MessageType MessageType { get; set; } = MessageType.Info;

    public string MessageSubject { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;
}
