namespace TechMES.Contracts.Messages;

/// <summary>
/// Тип изменения в модуле Messages.
///
/// Это значение отправляется через SignalR,
/// чтобы WEB понимал, почему нужно обновить список сообщений.
/// </summary>
public enum MessageChangedEventType
{
    /// <summary>
    /// Сообщение создано через Runtime.Service.
    /// </summary>
    Created = 0,

    /// <summary>
    /// Сообщение обновлено через Runtime.Service.
    /// </summary>
    Updated = 1,

    /// <summary>
    /// Сообщение удалено через Runtime.Service.
    /// </summary>
    Deleted = 2,

    /// <summary>
    /// Изменено состояние Active / Inactive.
    /// </summary>
    ActivityChanged = 3,

    /// <summary>
    /// Сообщение отмечено как просмотренное.
    /// </summary>
    Viewed = 4,

    /// <summary>
    /// Хранилище сообщений изменилось вне Runtime.Service.
    ///
    /// Например:
    /// - данные изменили напрямую в PostgreSQL;
    /// - старое WPF-приложение изменило таблицу;
    /// - другой сервис обновил Messages.
    /// </summary>
    StorageChanged = 5
}