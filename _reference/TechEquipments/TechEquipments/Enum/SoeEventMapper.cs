using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public static class SoeEventMapper
    {
        public static string GetDescription(this Enum value)
        {
            var fi = value.GetType().GetField(value.ToString());
            var attr = fi?.GetCustomAttribute<DescriptionAttribute>();
            return attr?.Description ?? value.ToString();
        }

        // Безопасно (если code вне диапазона)
        public static string GetDescriptionOrEmpty<TEnum>(int code) where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(typeof(TEnum), code)) return "";

            return ((Enum)(object)(TEnum)Enum.ToObject(typeof(TEnum), code)).GetDescription();
        }

        public static string GetEventText(string equipType, int bitCode) => EquipTypeRegistry.GetEventText(equipType, bitCode);

        private static string GetNameOrEmpty<TEnum>(int code) where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(typeof(TEnum), code)) return "";
            return Enum.GetName(typeof(TEnum), code) ?? "";
        }

        public static string GetEventKey(string equipType, int bitCode)
        {
            if (bitCode <= 0) return "";

            var t = (equipType ?? "").Trim();

            return t switch
            {
                "DigitalIn" or "DigitalInSiemens" => GetNameOrEmpty<DiSoeCode>(bitCode),
                "DigitalOut" or "DigitalOutSiemens" => GetNameOrEmpty<DoSoeCode>(bitCode),
                "Atv" or "AtvSiemens" => GetNameOrEmpty<AtvSoeCode>(bitCode),
                "Motor" or "MotorSiemens" => GetNameOrEmpty<MotorSoeCode>(bitCode),
                "AnalogIn" or "AnalogInCalc" or "AnalogInCalcSiemens" or "AnalogInSiemens" => GetNameOrEmpty<AiSoeCode>(bitCode),
                "ValveA" or "ValveASiemens" => GetNameOrEmpty<AoSoeCode>(bitCode),
                "ValveA_EL" => GetNameOrEmpty<VgaElSoeCode>(bitCode),
                "ValveD" or "ValveDSiemens" => GetNameOrEmpty<VgdSoeCode>(bitCode),
                _ => ""
            };
        }
    }

    #region Enum
    // ---------------- AI (32) ----------------
    public enum AiSoeCode : int
    {
        [Description("AL LA EN - Off")] AL_LA_EN_Off = 1,
        [Description("AL LW EN - Off")] AL_LW_EN_Off = 2,
        [Description("AL HW EN - Off")] AL_HW_EN_Off = 3,
        [Description("AL HA EN - Off")] AL_HA_EN_Off = 4,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 5,
        [Description("Reserve - Off")] Reserve_Off_22 = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("AL LA - Down")] AL_LA_Down = 9,
        [Description("AL LW - Down")] AL_LW_Down = 10,
        [Description("AL HW - Down")] AL_HW_Down = 11,
        [Description("AL HA - Down")] AL_HA_Down = 12,
        [Description("AL A - Down")] AL_A_Down = 13,
        [Description("AL W - Down")] AL_W_Down = 14,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,

        [Description("AL LA EN - On")] AL_LA_EN_On = 17,
        [Description("AL LW EN - On")] AL_LW_EN_On = 18,
        [Description("AL HW EN - On")] AL_HW_EN_On = 19,
        [Description("AL HA EN - On")] AL_HA_EN_On = 20,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 21,
        [Description("Reserve - On")] Reserve_On_06 = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("AL LA - Up")] AL_LA_Up = 25,
        [Description("AL LW - Up")] AL_LW_Up = 26,
        [Description("AL HW - Up")] AL_HW_Up = 27,
        [Description("AL HA - Up")] AL_HA_Up = 28,
        [Description("AL A - Up")] AL_A_Up = 29,
        [Description("AL W - Up")] AL_W_Up = 30,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32,
    }

    // ---------------- AO (32) ----------------
    public enum AoSoeCode : int
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("AL LA EN - Off")] AL_LA_EN_Off = 2,
        [Description("AL LW EN - Off")] AL_LW_EN_Off = 3,
        [Description("AL HW EN - Off")] AL_HW_EN_Off = 4,
        [Description("AL HA EN - Off")] AL_HA_EN_Off = 5,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("AL LA - Down")] AL_LA_Down = 9,
        [Description("AL LW - Down")] AL_LW_Down = 10,
        [Description("AL HW - Down")] AL_HW_Down = 11,
        [Description("AL HA - Down")] AL_HA_Down = 12,
        [Description("AL A - Down")] AL_A_Down = 13,
        [Description("AL W - Down")] AL_W_Down = 14,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,

        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("AL LA EN - On")] AL_LA_EN_On = 18,
        [Description("AL LW EN - On")] AL_LW_EN_On = 19,
        [Description("AL HW EN - On")] AL_HW_EN_On = 20,
        [Description("AL HA EN - On")] AL_HA_EN_On = 21,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("AL LA - Up")] AL_LA_Up = 25,
        [Description("AL LW - Up")] AL_LW_Up = 26,
        [Description("AL HW - Up")] AL_HW_Up = 27,
        [Description("AL HA - Up")] AL_HA_Up = 28,
        [Description("AL A - Up")] AL_A_Up = 29,
        [Description("AL W - Up")] AL_W_Up = 30,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32,
    }

    // ---------------- DI (32) ----------------
    public enum DiSoeCode : int
    {
        [Description("VALUE - Off")] VALUE_Off = 1,
        [Description("VALUE TRUE - Off")] VALUE_TRUE_Off = 2,
        [Description("VALUE FORCED - Off")] VALUE_FORCED_Off = 3,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 4,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 5,
        [Description("Reserve - Off")] Reserve_Off_22 = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("Reserve - Off")] Reserve_Off_25 = 9,
        [Description("Reserve - Off")] Reserve_Off_26 = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("Reserve - Off")] Reserve_Off_30 = 14,
        [Description("Reserve - Off")] Reserve_Off_31 = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,

        [Description("VALUE - On")] VALUE_On = 17,
        [Description("VALUE TRUE - On")] VALUE_TRUE_On = 18,
        [Description("VALUE FORCED - On")] VALUE_FORCED_On = 19,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 20,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 21,
        [Description("Reserve - On")] Reserve_On_06 = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("Reserve - On")] Reserve_On_09 = 25,
        [Description("Reserve - On")] Reserve_On_10 = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("Reserve - On")] Reserve_On_14 = 30,
        [Description("Reserve - On")] Reserve_On_15 = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32,
    }

    // ---------------- DO (32) ----------------
    public enum DoSoeCode : int
    {
        [Description("VALUE TRUE - Off")] VALUE_TRUE_Off = 1,
        [Description("VALUE - Off")] VALUE_Off = 2,
        [Description("VALUE FORCED - Off")] VALUE_FORCED_Off = 3,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 4,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 5,
        [Description("Reserve - Off")] Reserve_Off_22 = 6,
        [Description("Reserve - Off")] Reserve_Off_23 = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("Reserve - Off")] Reserve_Off_25 = 9,
        [Description("Reserve - Off")] Reserve_Off_26 = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("Reserve - Off")] Reserve_Off_30 = 14,
        [Description("Reserve - Off")] Reserve_Off_31 = 15,
        [Description("Reserve - Off")] Reserve_Off_32 = 16,

        [Description("VALUE TRUE - On")] VALUE_TRUE_On = 17,
        [Description("VALUE - On")] VALUE_On = 18,
        [Description("VALUE FORCED - On")] VALUE_FORCED_On = 19,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 20,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 21,
        [Description("Reserve - On")] Reserve_On_06 = 22,
        [Description("Reserve - On")] Reserve_On_07 = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("Reserve - On")] Reserve_On_09 = 25,
        [Description("Reserve - On")] Reserve_On_10 = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("Reserve - On")] Reserve_On_14 = 30,
        [Description("Reserve - On")] Reserve_On_15 = 31,
        [Description("Reserve - On")] Reserve_On_16 = 32,
    }

    // ---------------- ATV (64) ----------------
    public enum AtvSoeCode : int
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("AL LA EN - Off")] AL_LA_EN_Off = 2,
        [Description("AL LW EN - Off")] AL_LW_EN_Off = 3,
        [Description("AL HW EN - Off")] AL_HW_EN_Off = 4,
        [Description("AL HA EN - Off")] AL_HA_EN_Off = 5,
        [Description("FORCE CMD - Off")] FORCE_CMD_Off = 6,
        [Description("Reserve - Off")] Reserve_Off_39 = 7,
        [Description("Reserve - Off")] Reserve_Off_40 = 8,
        [Description("AL LA - Down")] AL_LA_Down = 9,
        [Description("AL LW - Down")] AL_LW_Down = 10,
        [Description("AL HW - Down")] AL_HW_Down = 11,
        [Description("AL HA - Down")] AL_HA_Down = 12,
        [Description("AL A - Down")] AL_A_Down = 13,
        [Description("AL W - Down")] AL_W_Down = 14,
        [Description("AL HEALTH - Down")] AL_HEALTH_Down = 15,
        [Description("Reserve - Off")] Reserve_Off_48 = 16,
        [Description("RUN - Off")] RUN_Off = 17,
        [Description("AL EN - Off")] AL_EN_Off = 18,
        [Description("AL - Down")] AL_Down = 19,
        [Description("START - Down")] START_Down = 20,
        [Description("STOP TYPE - Freewheel")] STOP_TYPE_Freewheel = 21,
        [Description("Reserve - Off")] Reserve_Off_54 = 22,
        [Description("Reserve - Off")] Reserve_Off_55 = 23,
        [Description("Reserve - Off")] Reserve_Off_56 = 24,
        [Description("Reserve - Off")] Reserve_Off_57 = 25,
        [Description("Reserve - Off")] Reserve_Off_58 = 26,
        [Description("Reserve - Off")] Reserve_Off_59 = 27,
        [Description("Reserve - Off")] Reserve_Off_60 = 28,
        [Description("Reserve - Off")] Reserve_Off_61 = 29,
        [Description("Reserve - Off")] Reserve_Off_62 = 30,
        [Description("Reserve - Off")] Reserve_Off_63 = 31,
        [Description("Reserve - Off")] Reserve_Off_64 = 32,

        [Description("MODE - Auto")] MODE_Auto = 33,
        [Description("AL LA EN - On")] AL_LA_EN_On = 34,
        [Description("AL LW EN - On")] AL_LW_EN_On = 35,
        [Description("AL HW EN - On")] AL_HW_EN_On = 36,
        [Description("AL HA EN - On")] AL_HA_EN_On = 37,
        [Description("FORCE CMD - On")] FORCE_CMD_On = 38,
        [Description("Reserve - On")] Reserve_On_07 = 39,
        [Description("Reserve - On")] Reserve_On_08 = 40,
        [Description("AL LA - Up")] AL_LA_Up = 41,
        [Description("AL LW - Up")] AL_LW_Up = 42,
        [Description("AL HW - Up")] AL_HW_Up = 43,
        [Description("AL HA - Up")] AL_HA_Up = 44,
        [Description("AL A - Up")] AL_A_Up = 45,
        [Description("AL W - Up")] AL_W_Up = 46,
        [Description("AL HEALTH - Up")] AL_HEALTH_Up = 47,
        [Description("Reserve - On")] Reserve_On_16 = 48,
        [Description("RUN - Up")] RUN_Up = 49,
        [Description("AL EN - On")] AL_EN_On = 50,
        [Description("AL - Up")] AL_Up = 51,
        [Description("START - Up")] START_Up = 52,
        [Description("STOP TYPE - Ramp")] STOP_TYPE_Ramp = 53,
        [Description("Reserve - On")] Reserve_On_22 = 54,
        [Description("Reserve - On")] Reserve_On_23 = 55,
        [Description("Reserve - On")] Reserve_On_24 = 56,
        [Description("Reserve - On")] Reserve_On_25 = 57,
        [Description("Reserve - On")] Reserve_On_26 = 58,
        [Description("Reserve - On")] Reserve_On_27 = 59,
        [Description("Reserve - On")] Reserve_On_28 = 60,
        [Description("Reserve - On")] Reserve_On_29 = 61,
        [Description("Reserve - On")] Reserve_On_30 = 62,
        [Description("Reserve - On")] Reserve_On_31 = 63,
        [Description("Reserve - On")] Reserve_On_32 = 64,
    }

    // ---------------- VGD (32) ----------------
    public enum VgdSoeCode : int
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("CMD - Off")] CMD_Off = 2,
        [Description("MAN - Off")] MAN_Off = 3,
        [Description("AL_EN - Off")] AL_EN_Off = 4,
        [Description("AL - Off")] AL_Off = 5,
        [Description("OPEN_AL - Off")] OPEN_AL_Off = 6,
        [Description("CLOSE_AL - Off")] CLOSE_AL_Off = 7,
        [Description("Reserve - Off")] Reserve_Off_24 = 8,
        [Description("OPENED - Off")] OPENED_Off = 9,
        [Description("CLOSED - Off")] CLOSED_Off = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("NOT_TRIP_DCS - Off")] NOT_TRIP_DCS_Off = 14,
        [Description("EMER_OFF - Off")] EMER_OFF_Off = 15,
        [Description("NOT_TRIP_EDS - Off")] NOT_TRIP_EDS_Off = 16,

        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("CMD - On")] CMD_On = 18,
        [Description("MAN - On")] MAN_On = 19,
        [Description("AL_EN - On")] AL_EN_On = 20,
        [Description("AL - On")] AL_On = 21,
        [Description("OPEN_AL - On")] OPEN_AL_On = 22,
        [Description("CLOSE_AL - On")] CLOSE_AL_On = 23,
        [Description("Reserve - On")] Reserve_On_08 = 24,
        [Description("OPENED - On")] OPENED_On = 25,
        [Description("CLOSED - On")] CLOSED_On = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("NOT_TRIP_DCS - On")] NOT_TRIP_DCS_On = 30,
        [Description("EMER_OFF - On")] EMER_OFF_On = 31,
        [Description("NOT_TRIP_EDS - On")] NOT_TRIP_EDS_On = 32,
    }

    // ---------------- Motor (32) ----------------
    public enum MotorSoeCode : int
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("CMD - Off")] CMD_Off = 2,
        [Description("MAN - Off")] MAN_Off = 3,
        [Description("AL_EN - Off")] AL_EN_Off = 4,
        [Description("AL - Off")] AL_Off = 5,
        [Description("T_WORK_AL - Off")] T_WORK_AL_Off = 6,
        [Description("T_WORK_AL_ACK - Off")] T_WORK_AL_ACK_Off = 7,
        [Description("TIME_RESET - Off")] TIME_RESET_Off = 8,
        [Description("Reserve - Off")] Reserve_Off_25 = 9,
        [Description("Reserve - Off")] Reserve_Off_26 = 10,
        [Description("Reserve - Off")] Reserve_Off_27 = 11,
        [Description("Reserve - Off")] Reserve_Off_28 = 12,
        [Description("Reserve - Off")] Reserve_Off_29 = 13,
        [Description("READY - Off")] READY_Off = 14,
        [Description("EMER_OFF - Off")] EMER_OFF_Off = 15,
        [Description("NOT_TRIP_EDS - Off")] NOT_TRIP_EDS_Off = 16,

        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("CMD - On")] CMD_On = 18,
        [Description("MAN - On")] MAN_On = 19,
        [Description("AL_EN - On")] AL_EN_On = 20,
        [Description("AL - On")] AL_On = 21,
        [Description("T_WORK_AL - On")] T_WORK_AL_On = 22,
        [Description("T_WORK_AL_ACK - On")] T_WORK_AL_ACK_On = 23,
        [Description("TIME_RESET - On")] TIME_RESET_On = 24,
        [Description("Reserve - On")] Reserve_On_09 = 25,
        [Description("Reserve - On")] Reserve_On_10 = 26,
        [Description("Reserve - On")] Reserve_On_11 = 27,
        [Description("Reserve - On")] Reserve_On_12 = 28,
        [Description("Reserve - On")] Reserve_On_13 = 29,
        [Description("READY - On")] READY_On = 30,
        [Description("EMER_OFF - On")] EMER_OFF_On = 31,
        [Description("NOT_TRIP_EDS - On")] NOT_TRIP_EDS_On = 32,
    }

    // ---------------- VgaEl (32) ----------------
    public enum VgaElSoeCode : int
    {
        [Description("MODE - Man")] MODE_Man = 1,
        [Description("OPEN_CMD - Off")] OPEN_CMD_Off = 2,
        [Description("CLOSE_CMD - Off")] CLOSE_CMD_Off = 3,
        [Description("AL_EN - Off")] AL_EN_Off = 4,
        [Description("AL - Off")] AL_Off = 5,
        [Description("OPEN AL - Off")] OPEN_AL_Off = 6,
        [Description("CLOSE AL - Off")] CLOSE_AL_Off = 7,
        [Description("RESERVE - Off")] RESERVE_Off_24 = 8,
        [Description("OPENED - Off")] OPENED_Off = 9,
        [Description("CLOSED - Off")] CLOSED_Off = 10,
        [Description("RESERVE - Off")] RESERVE_Off_27 = 11,
        [Description("SQ_EN - Off")] SQ_EN_Off = 12,
        [Description("ACTUATOR EN - Off")] ACTUATOR_EN_Off = 13,
        [Description("RESERVE - Off")] RESERVE_Off_30 = 14,
        [Description("RESERVE - Off")] RESERVE_Off_31 = 15,
        [Description("RESERVE - Off")] RESERVE_Off_32 = 16,

        [Description("MODE - Auto")] MODE_Auto = 17,
        [Description("OPEN_CMD - On")] OPEN_CMD_On = 18,
        [Description("CLOSE_CMD - On")] CLOSE_CMD_On = 19,
        [Description("AL_EN - On")] AL_EN_On = 20,
        [Description("AL - On")] AL_On = 21,
        [Description("OPEN AL - On")] OPEN_AL_On = 22,
        [Description("CLOSE AL - On")] CLOSE_AL_On = 23,
        [Description("RESERVE - On")] RESERVE_On_08 = 24,
        [Description("OPENED - On")] OPENED_On = 25,
        [Description("CLOSED - On")] CLOSED_On = 26,
        [Description("RESERVE - On")] RESERVE_On_11 = 27,
        [Description("SQ_EN - On")] SQ_EN_On = 28,
        [Description("ACTUATOR EN - On")] ACTUATOR_EN_On = 29,
        [Description("RESERVE - On")] RESERVE_On_14 = 30,
        [Description("RESERVE - On")] RESERVE_On_15 = 31,
        [Description("RESERVE - On")] RESERVE_On_16 = 32,
    }

    #endregion
}
