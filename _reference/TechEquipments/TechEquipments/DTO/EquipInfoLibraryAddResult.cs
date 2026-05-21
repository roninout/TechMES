using System.Collections.Generic;

namespace TechEquipments
{
    /// <summary>
    /// Результат добавления файлов в библиотеку Info.
    /// ResolvedAssets содержит и новые, и уже существующие файлы,
    /// которые можно сразу линковать к equipment.
    /// </summary>
    public sealed class EquipInfoLibraryAddResult
    {
        public List<EquipmentInfoFileDto> ResolvedAssets { get; } = new();
        public List<string> AddedToLibraryFileNames { get; } = new();
        public List<string> ExistingInLibraryFileNames { get; } = new();
    }
}
