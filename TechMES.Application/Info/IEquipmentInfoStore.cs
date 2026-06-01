using TechMES.Contracts.Info;

namespace TechMES.Application.Info;

/// <summary>
/// Application-интерфейс Info-хранилища.
/// Runtime.Service через него работает с карточкой оборудования, файлами, заметками, избранным и PDF view state,
/// не зная, что сейчас данные лежат в PostgreSQL-таблицах WPF/обслуживающего приложения.
/// </summary>
public interface IEquipmentInfoStore
{
    /// <summary>
    /// Загружает полную Info-карточку выбранного оборудования без бинарных данных файлов.
    /// </summary>
    Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default);

    /// <summary>
    /// Загружает легкие счетчики вложений/заметок для списка оборудования.
    /// </summary>
    Task<IReadOnlyList<EquipmentInfoSummaryDto>> GetSummariesAsync(
        IEnumerable<string> equipNames,
        CancellationToken ct = default);

    /// <summary>
    /// Сохраняет редактируемое описание оборудования.
    /// </summary>
    Task<EquipmentInfoDto> SaveDescriptionAsync(
        string equipName,
        string? description,
        CancellationToken ct = default);

    /// <summary>
    /// Возвращает избранное оборудование для конкретного устройства/пользователя.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetFavoriteEquipNamesAsync(
        string deviceName,
        CancellationToken ct = default);

    /// <summary>
    /// Добавляет или удаляет оборудование из избранного.
    /// </summary>
    Task SetFavoriteAsync(
        string equipName,
        bool isFavorite,
        string deviceName,
        CancellationToken ct = default);

    /// <summary>
    /// Загружает бинарное содержимое фото/PDF/схемы по идентификатору файла.
    /// </summary>
    Task<EquipmentInfoFileContentDto?> GetFileAsync(
        EquipmentInfoFileKind kind,
        long id,
        CancellationToken ct = default);

    /// <summary>
    /// Читает сохраненную страницу/zoom/якорь для PDF или схемы.
    /// </summary>
    Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(
        string equipName,
        EquipmentInfoFileKind kind,
        long fileId,
        CancellationToken ct = default);

    /// <summary>
    /// Сохраняет страницу/zoom/якорь просмотра PDF или схемы.
    /// </summary>
    Task<EquipmentInfoDocumentViewStateDto> SaveDocumentViewStateAsync(
        string equipName,
        SaveEquipmentInfoDocumentViewStateRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Добавляет новую заметку к оборудованию.
    /// </summary>
    Task<EquipmentInfoNoteDto> AddNoteAsync(
        string equipName,
        string noteText,
        string userName,
        CancellationToken ct = default);

    /// <summary>
    /// Обновляет существующую заметку.
    /// </summary>
    Task<EquipmentInfoNoteDto?> UpdateNoteAsync(
        string equipName,
        long noteId,
        string noteText,
        string userName,
        CancellationToken ct = default);

    /// <summary>
    /// Удаляет заметку по идентификатору.
    /// </summary>
    Task DeleteNoteAsync(
        string equipName,
        long noteId,
        CancellationToken ct = default);
}
