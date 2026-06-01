using TechMES.Application.Messages;
using TechMES.Contracts.Messages;

namespace TechMES.Runtime.Service.Messages;

/// <summary>
/// Временный in-memory адаптер для сообщений.
///
/// Это тоже адаптер, только он хранит данные в памяти процесса.
/// Благодаря интерфейсу IMessageStore Runtime Service не знает,
/// что именно подключено: память, PostgreSQL или другая БД.
/// </summary>
public sealed class InMemoryMessageStore : IMessageStore
{
    /// <summary>
    /// Защита коллекций от параллельного доступа нескольких HTTP-запросов.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Сообщения, хранящиеся только в памяти процесса Runtime.Service.
    /// </summary>
    private readonly List<EquipmentMessageDto> _messages = [];

    /// <summary>
    /// Отметки просмотра: message id -> набор устройств, которые прочитали сообщение.
    /// </summary>
    private readonly Dictionary<long, HashSet<string>> _viewsByMessageId = [];

    /// <summary>
    /// Следующий in-memory идентификатор сообщения.
    /// </summary>
    private long _nextId = 1;

    /// <summary>
    /// Добавляет стартовые тестовые сообщения.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_messages.Count > 0)
                return Task.CompletedTask;

            AddSeedMessage(
                MessageType.Info,
                "Runtime Service is ready",
                "Это тестовое сообщение создано временным InMemory-адаптером. " +
                "Следующим шагом мы заменим его на PostgreSQL-адаптер.",
                "RUNTIME-SERVICE");

            AddSeedMessage(
                MessageType.Warning,
                "Adapter pattern enabled",
                "Runtime Service теперь работает через IMessageStore. " +
                "Это значит, что конкретную БД можно будет заменить без изменения WEB-интерфейса.",
                "RUNTIME-SERVICE");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Возвращает сообщения, дополненные view-флагами относительно текущего устройства.
    /// </summary>
    public Task<IReadOnlyList<EquipmentMessageDto>> GetMessagesAsync(
        bool includeInactive,
        string deviceName,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var normalizedDeviceName = NormalizeDeviceName(deviceName);

            var result = _messages
                .Where(x => includeInactive || x.IsActive)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => CloneForDevice(x, normalizedDeviceName))
                .ToList();

            return Task.FromResult<IReadOnlyList<EquipmentMessageDto>>(result);
        }
    }

    /// <summary>
    /// Считает активные сообщения без создания полного DTO-списка.
    /// </summary>
    public Task<int> GetActiveMessageCountAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var count = _messages.Count(x => x.IsActive);
            return Task.FromResult(count);
        }
    }

    /// <summary>
    /// Создает новое сообщение или обновляет существующее.
    /// </summary>
    public Task<EquipmentMessageDto> SaveMessageAsync(
        SaveMessageRequest request,
        string userName,
        string deviceName,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var normalizedUserName = NormalizeDeviceName(userName);
            var now = DateTime.Now;

            if (request.Id <= 0)
            {
                var created = new EquipmentMessageDto
                {
                    Id = _nextId++,
                    MessageType = request.MessageType,
                    MessageSubject = request.MessageSubject?.Trim() ?? string.Empty,
                    MessageText = request.MessageText?.Trim() ?? string.Empty,
                    IsActive = true,
                    CreatedBy = normalizedUserName,
                    CreatedAt = now
                };

                _messages.Add(created);
                return Task.FromResult(CloneForDevice(created, NormalizeDeviceName(deviceName)));
            }

            var existing = _messages.FirstOrDefault(x => x.Id == request.Id);

            if (existing is null)
                throw new InvalidOperationException($"Message with Id={request.Id} was not found.");

            existing.MessageType = request.MessageType;
            existing.MessageSubject = request.MessageSubject?.Trim() ?? string.Empty;
            existing.MessageText = request.MessageText?.Trim() ?? string.Empty;
            existing.UpdatedBy = normalizedUserName;
            existing.UpdatedAt = now;

            return Task.FromResult(CloneForDevice(existing, NormalizeDeviceName(deviceName)));
        }
    }

    /// <summary>
    /// Переключает активность сообщения, если текущий пользователь является автором.
    /// </summary>
    public Task<bool> ToggleActivityAsync(
        long messageId,
        string userName,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var normalizedUserName = NormalizeDeviceName(userName);
            var message = _messages.FirstOrDefault(x => x.Id == messageId);

            if (message is null)
                return Task.FromResult(false);

            // Простое бизнес-правило: активность может менять только автор сообщения.
            // Позже здесь можно будет учитывать роли пользователя и права доступа.
            if (!string.Equals(message.CreatedBy, normalizedUserName, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);

            message.IsActive = !message.IsActive;
            message.UpdatedBy = normalizedUserName;
            message.UpdatedAt = DateTime.Now;

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Удаляет сообщение и его отметки просмотра.
    /// </summary>
    public Task DeleteMessageAsync(long messageId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _messages.RemoveAll(x => x.Id == messageId);
            _viewsByMessageId.Remove(messageId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Помечает сообщение просмотренным для текущего устройства.
    /// </summary>
    public Task MarkViewedAsync(long messageId, string deviceName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var normalizedDeviceName = NormalizeDeviceName(deviceName);

            if (!_messages.Any(x => x.Id == messageId))
                return Task.CompletedTask;

            var message = _messages.First(x => x.Id == messageId);
            if (string.Equals(message.CreatedBy, normalizedDeviceName, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            if (!_viewsByMessageId.TryGetValue(messageId, out var viewers))
            {
                viewers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _viewsByMessageId[messageId] = viewers;
            }

            viewers.Add(normalizedDeviceName);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Добавляет стартовое сообщение без прохода через публичный SaveMessageAsync.
    /// </summary>
    private void AddSeedMessage(
        MessageType type,
        string subject,
        string text,
        string createdBy)
    {
        _messages.Add(new EquipmentMessageDto
        {
            Id = _nextId++,
            MessageType = type,
            MessageSubject = subject,
            MessageText = text,
            IsActive = true,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now
        });
    }

    /// <summary>
    /// Клонирует сообщение и вычисляет поля просмотра для конкретного устройства.
    /// </summary>
    private EquipmentMessageDto CloneForDevice(
        EquipmentMessageDto source,
        string deviceName)
    {
        var viewers = _viewsByMessageId.TryGetValue(source.Id, out var set)
            ? set
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visibleViewers = viewers
            .Where(x => !string.Equals(x, source.CreatedBy, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var isAuthor = string.Equals(source.CreatedBy, deviceName, StringComparison.OrdinalIgnoreCase);

        return new EquipmentMessageDto
        {
            Id = source.Id,
            MessageType = source.MessageType,
            MessageSubject = source.MessageSubject,
            MessageText = source.MessageText,
            IsActive = source.IsActive,
            CreatedBy = source.CreatedBy,
            CreatedAt = source.CreatedAt,
            UpdatedBy = source.UpdatedBy,
            UpdatedAt = source.UpdatedAt,
            IsViewedByCurrentDevice = !isAuthor && visibleViewers.Contains(deviceName, StringComparer.OrdinalIgnoreCase),
            IsViewedByOtherDevice = isAuthor && visibleViewers.Count > 0,
            ViewedByText = string.Join(", ", visibleViewers)
        };
    }

    /// <summary>
    /// Нормализует имя устройства; пустое значение заменяет именем машины.
    /// </summary>
    private static string NormalizeDeviceName(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Environment.MachineName
            : value.Trim();
    }

    /// <summary>
    /// Возвращает компактный snapshot для MessageStorageWatcher.
    /// </summary>
    public Task<MessageStorageSnapshot> GetStorageSnapshotAsync(
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var totalCount = _messages.Count;
            var activeCount = _messages.Count(x => x.IsActive);
            var viewCount = _viewsByMessageId.Values.Sum(x => x.Count);

            DateTime? lastMessageChangedAt = _messages.Count == 0
                ? null
                : _messages.Max(x => x.UpdatedAt ?? x.CreatedAt);

            return Task.FromResult(new MessageStorageSnapshot
            {
                TotalMessageCount = totalCount,
                ActiveMessageCount = activeCount,
                ViewCount = viewCount,
                LastMessageChangedAt = lastMessageChangedAt,
                LastViewedAt = null
            });
        }
    }
}
