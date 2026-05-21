namespace TechEquipments
{
    /// <summary>
    /// Итог массовой генерации QR.
    /// </summary>
    public sealed class BulkQrGenerateResult
    {
        public int Total { get; set; }
        public int Created { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }
}