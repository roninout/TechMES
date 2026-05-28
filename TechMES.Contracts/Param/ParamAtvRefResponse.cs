namespace TechMES.Contracts.Param;

public sealed class ParamAtvRefResponse
{
    public string EquipmentName { get; set; } = "";

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;

    public string? AtvEquipmentName { get; set; }

    public bool IsLinkedFromMotor { get; set; }

    public ParamLinkedParamDto? AtvEquipment { get; set; }
}
