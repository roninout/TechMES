using System;

namespace TechEquipments
{
    /// <summary>
    /// Сохранённая позиция просмотра PDF для конкретного equipment и документа.
    /// Храним:
    /// - equipment
    /// - тип страницы (Instruction / Scheme)
    /// - file id из library
    /// - файл для читаемости/отладки
    /// - page + zoom + anchor point внутри PDF
    /// </summary>
    public sealed class EquipmentInfoDocumentViewStateDto
    {
        public string EquipName { get; set; } = "";

        public InfoPageKind InfoPageKind { get; set; }

        public long FileId { get; set; }

        public string FileName { get; set; } = "";

        public int PageNumber { get; set; }

        public double ZoomFactor { get; set; }

        public double AnchorX { get; set; }

        public double AnchorY { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}