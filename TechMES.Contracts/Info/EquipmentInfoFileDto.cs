namespace TechMES.Contracts.Info;

public sealed class EquipmentInfoFileDto
{
    public long Id { get; set; }

    public string EquipName { get; set; } = string.Empty;

    public string EquipTypeGroupKey { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string FileHash { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public EquipmentInfoFileKind Kind { get; set; }

    public string ContentType { get; set; } = "application/octet-stream";

    public string ContentUrl { get; set; } = string.Empty;
}
