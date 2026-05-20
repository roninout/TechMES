namespace TechMES.Application.Messages;

/// <summary>
/// Лёгкий "отпечаток" состояния хранилища сообщений.
///
/// Watcher не должен каждые 30 секунд читать все сообщения целиком.
/// Вместо этого он читает только агрегированные значения:
/// количество сообщений, количество активных сообщений, последнее время изменения.
///
/// Если snapshot изменился — значит, данные в БД изменились,
/// и Runtime.Service должен отправить SignalR-событие WEB-клиентам.
/// </summary>
public sealed record MessageStorageSnapshot
{
    /// <summary>
    /// Общее количество сообщений.
    /// </summary>
    public long TotalMessageCount { get; init; }

    /// <summary>
    /// Количество активных сообщений.
    /// </summary>
    public long ActiveMessageCount { get; init; }

    /// <summary>
    /// Количество записей просмотра сообщений.
    /// </summary>
    public long ViewCount { get; init; }

    /// <summary>
    /// Последнее изменение в таблице сообщений.
    /// </summary>
    public DateTime? LastMessageChangedAt { get; init; }

    /// <summary>
    /// Последнее изменение в таблице просмотров.
    /// </summary>
    public DateTime? LastViewedAt { get; init; }
}