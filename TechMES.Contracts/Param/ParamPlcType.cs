namespace TechMES.Contracts.Param;

/// <summary>
/// <summary>
/// UI-тип строки PLC reference page.
/// Plant SCADA хранит это значение в EquipRef CUSTOM1.
/// </summary>
public enum ParamPlcType
{
    /// <summary>
    /// Тип не распознан.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Read-only check.
    /// </summary>
    EqCheck,

    /// <summary>
    /// Read/write check.
    /// </summary>
    EqCheckRW,

    /// <summary>
    /// Только отображение check-состояния.
    /// </summary>
    EqCheckDisplay,

    /// <summary>
    /// Read-only numeric value.
    /// </summary>
    EqNumR,

    /// <summary>
    /// Writable numeric value.
    /// </summary>
    EqNumW,

    /// <summary>
    /// Button-команда.
    /// </summary>
    EqButton,

    /// <summary>
    /// Button Up-команда.
    /// </summary>
    EqButtonUp,

    /// <summary>
    /// Button Down-команда.
    /// </summary>
    EqButtonDown,

    /// <summary>
    /// Button Mode-команда.
    /// </summary>
    EqButtonMode,

    /// <summary>
    /// Button Start/Stop-команда.
    /// </summary>
    EqButtonStartStop,

    /// <summary>
    /// Digital state.
    /// </summary>
    EqDigital,

    /// <summary>
    /// Digital input/output state.
    /// </summary>
    EqDigitalInOut,

    /// <summary>
    /// Motor status row.
    /// </summary>
    EqMotorStatus,

    /// <summary>
    /// Valve status row.
    /// </summary>
    EqValveStatus
}
