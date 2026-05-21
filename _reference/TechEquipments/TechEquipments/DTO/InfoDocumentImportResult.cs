using System;
using System.Collections.Generic;
using System.Linq;

namespace TechEquipments
{
    public sealed class InfoDocumentImportResult
    {
        public InfoFileKind Kind { get; set; }

        public string SheetName { get; set; } = "";

        // SCHEME import counters
        public int RowsScanned { get; set; }
        public int StationRowsScanned { get; set; }
        public int GroupRowsScanned { get; set; }
        public int EquipmentRowsScanned { get; set; }
        public int FileReferencesScanned { get; set; }
        public int ImportJobs { get; set; }

        public int AddedToDb { get; set; }
        public int UpdatedInDb { get; set; }
        public int LinkedExisting { get; set; }
        public int AlreadyLinked { get; set; }

        public int MissingFiles { get; set; }
        public int MissingGroups { get; set; }
        public int MissingEquipments { get; set; }
        public int RowsWithoutEquipment { get; set; }
        public int EmptyRowsSkipped { get; set; }

        // INSTRUCTION import counters
        public int InstructionEquipmentRowsScanned { get; set; }
        public int InstructionOrderRowsScanned { get; set; }
        public int InstructionSupplierRowsScanned { get; set; }

        public int SuppliersUpserted { get; set; }
        public int OrdersUpserted { get; set; }
        public int EquipmentInfoUpdated { get; set; }

        public int ProductCodesEmpty { get; set; }
        public int ProductCodesNotFound { get; set; }

        public int PdfFileReferences { get; set; }
        public int ImageFileReferences { get; set; }

        public int PdfImportJobs { get; set; }
        public int ImageImportJobs { get; set; }

        public int PdfAddedToDb { get; set; }
        public int PdfUpdatedInDb { get; set; }
        public int PdfLinksCreated { get; set; }
        public int PdfAlreadyLinked { get; set; }

        public int ImageAddedToDb { get; set; }
        public int ImageUpdatedInDb { get; set; }
        public int ImageLinksCreated { get; set; }
        public int ImageAlreadyLinked { get; set; }

        public int MissingSourceFiles { get; set; }
        public int MissingImageFiles { get; set; }
        public int MissingSupplierLogos { get; set; }

        public int Errors { get; set; }

        public HashSet<string> AffectedEquipNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> PdfAffectedEquipNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ImageAffectedEquipNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> UpdatedEquipmentInfoNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> ErrorMessages { get; } = new();

        public string ToMessage(string title)
        {
            if (Kind == InfoFileKind.Instruction)
                return ToInstructionMessage(title);

            return ToSchemeMessage(title);
        }

        private string ToSchemeMessage(string title)
        {
            var msg =
                $"{title} completed.\n\n" +
                $"Sheet: {SheetName}\n\n" +
                $"Rows scanned: {RowsScanned}\n" +
                $"Station rows: {StationRowsScanned}\n" +
                $"Group rows: {GroupRowsScanned}\n" +
                $"Equipment rows: {EquipmentRowsScanned}\n" +
                $"File references scanned: {FileReferencesScanned}\n" +
                $"Import jobs: {ImportJobs}\n\n" +
                $"Added to DB: {AddedToDb}\n" +
                $"Updated in DB: {UpdatedInDb}\n" +
                $"Links created: {LinkedExisting}\n" +
                $"Already linked: {AlreadyLinked}\n\n" +
                $"Missing files: {MissingFiles}\n" +
                $"Missing groups: {MissingGroups}\n" +
                $"Missing equipments: {MissingEquipments}\n" +
                $"Rows without equipment: {RowsWithoutEquipment}\n" +
                $"Empty rows skipped: {EmptyRowsSkipped}\n" +
                $"Errors: {Errors}";

            if (ErrorMessages.Count > 0)
            {
                msg += "\n\nFirst messages:\n" +
                       string.Join("\n", ErrorMessages.Take(15));
            }

            return msg;
        }

        private string ToInstructionMessage(string title)
        {
            var msg =
                $"{title} completed.\n\n" +
                $"Sheet: {SheetName}\n\n" +
                $"Equipment rows scanned: {InstructionEquipmentRowsScanned}\n" +
                $"Orders rows scanned: {InstructionOrderRowsScanned}\n" +
                $"Supplier rows scanned: {InstructionSupplierRowsScanned}\n\n" +
                $"Suppliers upserted: {SuppliersUpserted}\n" +
                $"Orders upserted: {OrdersUpserted}\n" +
                $"Equipment info updated: {EquipmentInfoUpdated}\n\n" +
                $"PDF references: {PdfFileReferences}\n" +
                $"PDF import jobs: {PdfImportJobs}\n" +
                $"PDF added to DB: {PdfAddedToDb}\n" +
                $"PDF updated in DB: {PdfUpdatedInDb}\n" +
                $"PDF links created: {PdfLinksCreated}\n" +
                $"PDF already linked: {PdfAlreadyLinked}\n\n" +
                $"Image references: {ImageFileReferences}\n" +
                $"Image import jobs: {ImageImportJobs}\n" +
                $"Images added to DB: {ImageAddedToDb}\n" +
                $"Images updated in DB: {ImageUpdatedInDb}\n" +
                $"Image links created: {ImageLinksCreated}\n" +
                $"Images already linked: {ImageAlreadyLinked}\n\n" +
                $"Empty product code: {ProductCodesEmpty}\n" +
                $"Product code not found: {ProductCodesNotFound}\n" +
                $"Missing equipments: {MissingEquipments}\n" +
                $"Missing source files: {MissingSourceFiles}\n" +
                $"Missing image files: {MissingImageFiles}\n" +
                $"Missing supplier logos: {MissingSupplierLogos}\n" +
                $"Errors: {Errors}";

            if (ErrorMessages.Count > 0)
            {
                msg += "\n\nFirst messages:\n" +
                       string.Join("\n", ErrorMessages.Take(15));
            }

            return msg;
        }
    }

    public enum InfoDocumentImportDbStatus
    {
        AddedToDbAndLinked,
        UpdatedExistingAndLinked,
        LinkedExisting,
        AlreadyLinked
    }

    public sealed class InfoDocumentImportDbResult
    {
        public long DocumentId { get; init; }
        public InfoDocumentImportDbStatus Status { get; init; }
    }

    public sealed class InfoDocumentBulkImportDbResult
    {
        public long DocumentId { get; init; }

        public bool AddedToDb { get; init; }
        public bool UpdatedInDb { get; init; }

        public int LinksCreated { get; init; }
        public int AlreadyLinked { get; init; }

        public IReadOnlyCollection<string> AffectedEquipNames { get; init; } =
            Array.Empty<string>();
    }

    public sealed class InfoPhotoBulkImportDbResult
    {
        public long PhotoId { get; init; }

        public bool AddedToDb { get; init; }
        public bool UpdatedInDb { get; init; }

        public int LinksCreated { get; init; }
        public int AlreadyLinked { get; init; }

        public IReadOnlyCollection<string> AffectedEquipNames { get; init; } =
            Array.Empty<string>();
    }
}