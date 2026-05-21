namespace TechEquipments
{
    /// <summary>
    /// Простая модель доступной камеры для QR-окна.
    /// </summary>
    public sealed class QrCameraInfo
    {
        /// <summary>Индекс камеры в OpenCV.</summary>
        public int Index { get; set; }

        /// <summary>Текст для ComboBox.</summary>
        public string DisplayName { get; set; } = "";

        public override string ToString() => DisplayName;
    }
}