using TechMES.Contracts.Info;

namespace TechMES.Application.Info;

public interface IEquipmentInfoStore
{
    Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default);

    Task<EquipmentInfoDto> SaveDescriptionAsync(
        string equipName,
        string? description,
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
