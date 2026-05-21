using System.Collections.Generic;

namespace TechEquipments
{
    public sealed class InfoOrderCatalogDto
    {
        public string Type { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string Supplier { get; set; } = "";
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public string Image { get; set; } = "";

        public List<string> Sources { get; } = new();
        public List<string> Images { get; } = new();
    }
}