using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Тип элемента PLC (берём из CUSTOM1 поля EquipRef).
    /// </summary>
    public enum PlcTypeCustom
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
}
