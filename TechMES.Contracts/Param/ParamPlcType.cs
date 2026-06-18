namespace TechMES.Contracts.Param;

/// <summary>
/// UI-тип строки PLC reference page.
/// Plant SCADA хранит это значение в EquipRef поле CUSTOM1, а WEB по нему выбирает
/// конкретный визуальный элемент: switch, numeric/text field, button или status field.
/// </summary>
public enum ParamPlcType
{
    /// <summary>
    /// Тип не распознан или CUSTOM1 пустой.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Boolean switch, который WPF разрешал редактировать.
    /// </summary>
    EqCheck,

    /// <summary>
    /// Boolean read/write switch.
    /// </summary>
    EqCheckRW,

    /// <summary>
    /// Только отображение boolean switch-состояния.
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
    /// Read-only enum/text value. В WPF отображался тем же TextEdit-шаблоном, что EqNumR.
    /// </summary>
    EqEnum,

    /// <summary>
    /// Read-only enum/text display value. В WEB отображается как EqNumR.
    /// </summary>
    EqEnumDisplay,

    /// <summary>
    /// Boolean command button with On/Off text.
    /// </summary>
    EqButton,

    /// <summary>
    /// Button Up command.
    /// </summary>
    EqButtonUp,

    /// <summary>
    /// Button Down command.
    /// </summary>
    EqButtonDown,

    /// <summary>
    /// Mode button. WPF displayed it as Manual/Auto.
    /// </summary>
    EqButtonMode,

    /// <summary>
    /// Start/Stop button.
    /// </summary>
    EqButtonStartStop,

    /// <summary>
    /// Digital state shown as on/off status rectangle.
    /// </summary>
    EqDigital,

    /// <summary>
    /// Digital input/output state shown as on/off status rectangle.
    /// </summary>
    EqDigitalInOut,

    /// <summary>
    /// Motor status code. WEB maps numeric codes to stop/run/alarm/starting/stopping/waiting.
    /// </summary>
    EqMotorStatus,

    /// <summary>
    /// Valve status code. WEB maps numeric codes to closed/opened/opening/closing/alarm.
    /// </summary>
    EqValveStatus
}
