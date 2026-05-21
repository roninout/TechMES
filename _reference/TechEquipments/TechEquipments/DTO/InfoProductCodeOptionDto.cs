namespace TechEquipments
{
    public sealed class InfoProductCodeOptionDto
    {
        public string Type { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string Supplier { get; set; } = "";
        public string Description { get; set; } = "";
        public string? SupplierLogoCachePath { get; set; }
    }
}