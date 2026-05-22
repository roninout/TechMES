namespace TechMES.Contracts.Info;

public sealed class SaveEquipmentInfoDocumentViewStateRequest
{
    public EquipmentInfoFileKind Kind { get; set; }

    public long FileId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public int PageNumber { get; set; } = 1;

    public double ZoomFactor { get; set; } = 100;

    public double AnchorX { get; set; }

    public double AnchorY { get; set; }
}
