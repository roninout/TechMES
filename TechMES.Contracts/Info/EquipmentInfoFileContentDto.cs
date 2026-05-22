namespace TechMES.Contracts.Info;

public sealed class EquipmentInfoFileContentDto
{
    public long Id { get; set; }

    public EquipmentInfoFileKind Kind { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string FileHash { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public byte[] FileData { get; set; } = [];

    public DateTime? UpdatedAt { get; set; }
}
