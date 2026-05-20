using TechMES.Contracts.Messages;

namespace TechMES.Application.Messages;

/// <summary>
/// Абстракция хранилища сообщений.
///
/// Это главный шаг к adapter-подходу:
/// Runtime Service будет работать только с этим интерфейсом и не будет знать,
/// где физически лежат сообщения — в памяти, PostgreSQL, SQL Server или другой БД.
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Подготовить хранилище к работе.
    ///
    /// Для PostgreSQL здесь позже можно будет создать таблицы, если их нет.
    /// Для InMemory-адаптера этот метод ничего не делает.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить список сообщений.
    /// </summary>
    Task<IReadOnlyList<EquipmentMessageDto>> GetMessagesAsync(
        bool includeInactive,
        string deviceName,
        CancellationToken ct = default);

    /// <summary>
    /// Получить количество активных сообщений.
    /// </summary>
    Task<int> GetActiveMessageCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Создать новое сообщение или обновить существующее.
    /// </summary>
    Task<EquipmentMessageDto> SaveMessageAsync(
        SaveMessageRequest request,
        string userName,
        string deviceName,
        CancellationToken ct = default);

    /// <summary>
    /// Переключить Active / Inactive.
    /// Возвращает false, если действие запрещено бизнес-правилом.
    /// Например: менять активность может только автор сообщения.
    /// </summary>
    Task<bool> ToggleActivityAsync(
        long messageId,
        string userName,
        CancellationToken ct = default);

    /// <summary>
    /// Удалить сообщение.
    /// </summary>
    Task DeleteMessageAsync(
        long messageId,
        CancellationToken ct = default);

    /// <summary>
    /// Отметить сообщение как просмотренное конкретным устройством/пользователем.
    /// </summary>
    Task MarkViewedAsync(
        long messageId,
        string deviceName,
        CancellationToken ct = default);

    /// <summary>
    /// Получить лёгкий отпечаток состояния хранилища сообщений.
    ///
    /// Этот метод нужен не для отображения данных в WEB,
    /// а для фонового watcher-а Runtime.Service.
    /// Watcher сравнивает старый и новый snapshot.
    /// Если они отличаются — значит БД изменилась.
    /// </summary>
    Task<MessageStorageSnapshot> GetStorageSnapshotAsync(
        CancellationToken ct = default);
}
