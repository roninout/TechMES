namespace TechMES.Contracts.Param;

/// <summary>
/// UI type of a PLC reference row. Plant SCADA stores it in EquipRef CUSTOM1.
/// </summary>
public enum ParamPlcType
{
    Unknown = 0,

    EqCheck,
    EqCheckRW,
    EqCheckDisplay,

    EqNumR,
    EqNumW,

    EqButton,
    EqButtonUp,
    EqButtonDown,
    EqButtonMode,
    EqButtonStartStop,

    EqDigital,
    EqDigitalInOut,

    EqMotorStatus,
    EqValveStatus
}
