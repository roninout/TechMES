using TechMES.Contracts.Info;

namespace TechMES.Application.Info;

public interface IEquipmentInfoStore
{
    Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default);

    Task<IReadOnlyList<EquipmentInfoSummaryDto>> GetSummariesAsync(
        IEnumerable<string> equipNames,
        CancellationToken ct = default);

    Task<EquipmentInfoDto> SaveDescriptionAsync(
        string equipName,
        string? description,
        CancellationToken ct = default);

    Task<IReadOnlyCollection<string>> GetFavoriteEquipNamesAsync(
        string deviceName,
        CancellationToken ct = default);

    Task SetFavoriteAsync(
        string equipName,
        bool isFavorite,
        string deviceName,
        CancellationToken ct = default);

    Task<EquipmentInfoFileContentDto?> GetFileAsync(
        EquipmentInfoFileKind kind,
        long id,
        CancellationToken ct = default);

    Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(
        string equipName,
        EquipmentInfoFileKind kind,
        long fileId,
        CancellationToken ct = default);

    Task<EquipmentInfoDocumentViewStateDto> SaveDocumentViewStateAsync(
        string equipName,
        SaveEquipmentInfoDocumentViewStateRequest request,
        CancellationToken ct = default);

    Task<EquipmentInfoNoteDto> AddNoteAsync(
        string equipName,
        string noteText,
        string userName,
        CancellationToken ct = default);

    Task<EquipmentInfoNoteDto?> UpdateNoteAsync(
        string equipName,
        long noteId,
        string noteText,
        string userName,
        CancellationToken ct = default);

    Task DeleteNoteAsync(
        string equipName,
        long noteId,
        CancellationToken ct = default);
}
