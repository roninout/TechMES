using System;

namespace TechEquipments
{
    /// <summary>
    /// Какая секция настроек сейчас открыта на Param (VGDParamView).
    /// </summary>
    public enum ParamSettingsPage
    {
        None = 0,
        Plc = 1,
        DiDo = 2,
        Alarm = 3,
        TimeWork = 4,
        DryRun = 5,
        Atv = 6,
    }
}
