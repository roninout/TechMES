using System;
using System.Collections.Generic;
using System.Linq;

namespace TechEquipments
{
    /// <summary>
    /// Результат умного импорта изображений в Info / Images.
    /// </summary>
    public sealed class InfoImageImportResult
    {
        public int ScannedFiles { get; set; }
        public int AddedToDb { get; set; }
        public int LinkedExisting { get; set; }
        public int AlreadyLinked { get; set; }
        public int SkippedNoEquipmentMatch { get; set; }
        public int SkippedAmbiguousMatch { get; set; }
        public int SkippedUnsupportedTypeFolder { get; set; }
        public int Errors { get; set; }

        public HashSet<string> AffectedEquipNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> ErrorMessages { get; } = new();

        public string ToMessage()
        {
            var msg =
                "Image import completed.\n\n" +
                $"Scanned files: {ScannedFiles}\n" +
                $"Added to DB: {AddedToDb}\n" +
                $"Linked existing: {LinkedExisting}\n" +
                $"Already linked: {AlreadyLinked}\n" +
                $"No matching equipment: {SkippedNoEquipmentMatch}\n" +
                $"Ambiguous equipment match: {SkippedAmbiguousMatch}\n" +
                $"Unsupported type folders: {SkippedUnsupportedTypeFolder}\n" +
                $"Errors: {Errors}";

            if (ErrorMessages.Count > 0)
            {
                msg += "\n\nFirst errors:\n" +
                       string.Join("\n", ErrorMessages.Take(10));
            }

            return msg;
        }
    }

    public enum InfoPhotoImportDbStatus
    {
        AddedToDbAndLinked,
        LinkedExisting,
        AlreadyLinked
    }

    public sealed class InfoPhotoImportDbResult
    {
        public InfoPhotoImportDbStatus Status { get; init; }
        public long PhotoId { get; init; }
    }
}