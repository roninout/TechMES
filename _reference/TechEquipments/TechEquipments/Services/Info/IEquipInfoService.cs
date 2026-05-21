using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Сервис карточки Info по оборудованию.
    /// </summary>
    public interface IEquipInfoService
    {
        Task EnsureTableAsync(CancellationToken ct = default);

        Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default);

        Task SaveAsync(EquipmentInfoDto model, CancellationToken ct = default);

        Task<IReadOnlyList<EquipmentInfoFileDto>> GetLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, CancellationToken ct = default);

        Task<EquipInfoLibraryAddResult> AddFilesToLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, IEnumerable<string> filePaths, CancellationToken ct = default);

        Task<EquipmentInfoFileDto?> GetLibraryFileByIdAsync(InfoFileKind kind, long id, CancellationToken ct = default);

        Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(string equipName, InfoPageKind pageKind, long fileId, CancellationToken ct = default);

        Task SaveDocumentViewStateAsync(EquipmentInfoDocumentViewStateDto model, CancellationToken ct = default);

        Task<bool> DeleteLibraryFileAsync(InfoFileKind kind, long id, CancellationToken ct = default);

        Task<IReadOnlyCollection<string>> GetFavoriteEquipNamesAsync(CancellationToken ct = default);

        Task<IReadOnlyCollection<string>> GetEquipNamesWithLinkedPhotosAsync(CancellationToken ct = default);

        Task<IReadOnlyCollection<string>> GetEquipNamesWithLinkedInstructionsAsync(CancellationToken ct = default);

        Task<IReadOnlyCollection<string>> GetEquipNamesWithLinkedSchemesAsync(CancellationToken ct = default);

        Task<IReadOnlyCollection<string>> GetEquipNamesWithNotesAsync(CancellationToken ct = default);

        Task SetFavoriteAsync(string equipName, bool isFavorite, CancellationToken ct = default);

        Task EnsureDatabaseAndTablesAsync(CancellationToken ct = default);

        Task<int> UpsertSuppliersAsync(IEnumerable<InstructionSupplierRow> suppliers, CancellationToken ct = default);

        Task<int> UpsertOrdersAsync(IEnumerable<InstructionOrderRow> orders, CancellationToken ct = default);

        Task<IReadOnlyDictionary<string, InfoOrderCatalogDto>> GetOrdersByProductCodesAsync(IEnumerable<string> productCodes, CancellationToken ct = default);

        Task<int> ApplyProductCodesToEquipmentInfoAsync(IEnumerable<EquipmentProductCodeLinkDto> links, CancellationToken ct = default);

        Task<InfoPhotoImportDbResult> ImportPhotoForEquipmentAsync(string equipName, string equipTypeGroup, string filePath, CancellationToken cancellationToken = default);

        Task<InfoPhotoBulkImportDbResult> ImportPhotoForEquipmentsAsync(string equipTypeGroup, string filePath, IEnumerable<string> equipNames, CancellationToken cancellationToken = default);

        Task<InfoDocumentImportDbResult> ImportDocumentForEquipmentAsync(InfoFileKind kind, string equipName, string equipTypeGroup, string filePath, CancellationToken cancellationToken = default);

        Task<InfoDocumentBulkImportDbResult> ImportDocumentForEquipmentsAsync(InfoFileKind kind, string equipTypeGroup, string filePath, IEnumerable<string> equipNames, CancellationToken cancellationToken = default);

        /// <summary> Получить список Product code из equip_order для указанного типа оборудования. </summary>
        Task<IReadOnlyList<InfoProductCodeOptionDto>> GetProductCodeOptionsAsync(string equipTypeGroupKey, CancellationToken ct = default);

        Task<IReadOnlyList<EquipmentInfoNoteDto>> GetNotesAsync(string equipName, CancellationToken ct = default);

        Task<IReadOnlyList<EquipmentInfoNoteDto>> SaveNotesAsync(string equipName, IEnumerable<EquipmentInfoNoteDto> notes, string userName, CancellationToken ct = default);

        Task DeleteNoteAsync(long noteId, CancellationToken ct = default);
    }
}