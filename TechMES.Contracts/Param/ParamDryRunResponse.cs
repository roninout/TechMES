namespace TechMES.Contracts.Param;

public sealed class ParamDryRunResponse
{
    public string EquipmentName { get; set; } = "";

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;

    public string? DryRunEquipmentName { get; set; }

    public ParamLinkedParamDto? DryRunEquipment { get; set; }

    public ParamDiDoRefDto? LinkedDi { get; set; }

    public ParamLinkedParamDto? LinkedAi { get; set; }
}
