namespace TechMES.Contracts.Param;

public sealed class ParamPlcRefsResponse
{
    public string EquipmentName { get; set; } = "";

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;

    public List<ParamPlcRefDto> Rows { get; set; } = [];
}
