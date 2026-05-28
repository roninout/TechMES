namespace TechMES.Contracts.Param;

public sealed class ParamDiDoRefsResponse
{
    public string EquipmentName { get; set; } = "";

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;

    public List<ParamDiDoRefDto> DiRows { get; set; } = [];

    public List<ParamDiDoRefDto> DoRows { get; set; } = [];
}
