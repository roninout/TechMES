namespace TechMES.Contracts.Soe;

public sealed class SoeResponse
{
    public string EquipmentName { get; set; } = "";

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public int TotalLoaded { get; set; }

    public DateTime LoadedAt { get; set; } = DateTime.Now;

    public List<SoeEventDto> Rows { get; set; } = [];
}
