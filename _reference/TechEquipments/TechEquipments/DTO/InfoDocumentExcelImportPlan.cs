using System.Collections.Generic;

namespace TechEquipments
{
    public sealed class InfoDocumentExcelImportPlan
    {
        public string SheetName { get; set; } = "";

        public List<StationSourceRow> StationRows { get; } = new();
        public List<GroupSourceRow> GroupRows { get; } = new();
        public List<EquipmentSourceRow> EquipmentRows { get; } = new();

        public string InstructionBaseFolder { get; set; } = "";
        public string InstructionImagesFolder { get; set; } = "";
        public string SupplierLogoFolder { get; set; } = "";

        public List<InstructionEquipmentRow> InstructionEquipmentRows { get; } = new();
        public List<InstructionOrderRow> InstructionOrderRows { get; } = new();
        public List<InstructionSupplierRow> InstructionSupplierRows { get; } = new();
    }

    public sealed class StationSourceRow
    {
        public int RowNumber { get; set; }
        public string BaseFolder { get; set; } = "";
        public string Station { get; set; } = "";
        public List<string> Sources { get; } = new();
    }

    public sealed class GroupSourceRow
    {
        public int RowNumber { get; set; }
        public string BaseFolder { get; set; } = "";
        public List<string> Groups { get; } = new();
        public List<string> Sources { get; } = new();
    }

    public sealed class EquipmentSourceRow
    {
        public int RowNumber { get; set; }
        public string BaseFolder { get; set; } = "";
        public List<string> Equipments { get; } = new();
        public List<string> Sources { get; } = new();
    }

    public sealed class InstructionEquipmentRow
    {
        public int RowNumber { get; set; }

        public string Station { get; set; } = "";
        public string Type { get; set; } = "";
        public string Equipment { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string Supplier { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public sealed class InstructionOrderRow
    {
        public int RowNumber { get; set; }

        public string Type { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string Supplier { get; set; } = "";
        public string Source { get; set; } = "";
        public string Description { get; set; } = "";
        public string Image { get; set; } = "";

        public List<string> Sources { get; } = new();
        public List<string> Images { get; } = new();
    }

    public sealed class InstructionSupplierRow
    {
        public int RowNumber { get; set; }

        public string Supplier { get; set; } = "";
        public string SupplierLogo { get; set; } = "";
        public string SupplierLogoPath { get; set; } = "";
    }
}