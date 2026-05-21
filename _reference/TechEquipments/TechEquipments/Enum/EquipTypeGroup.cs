using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace TechEquipments
{
    public enum EquipTypeGroup
    {
        All = 0,
        Equipment, //  EquipGroup -> child equipment
        VGA,       // ValveA
        VGA_EL,    // ValveA_EL
        VGD,       // ValveD
        Motor,     // Motor
        AI,        // AnalogIn, AnalogInCalc
        DI,        // DigitalIn
        DO,        // DigitalOut
        Atv,        // Atv
        Favorites  // только фильтр UI
    }

    public static class EquipTypeRegistry
    {

        private readonly record struct Meta(EquipTypeGroup Group, Func<int, string> EventText);

        // ЕДИНЫЙ СПРАВОЧНИК: Type -> (Group + как декодировать bitCode)
        private static readonly Dictionary<string, Meta> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Equipment"] = new(EquipTypeGroup.Equipment, _ => ""),

            ["DigitalIn"] = new(EquipTypeGroup.DI, c => SoeEventMapper.GetDescriptionOrEmpty<DiSoeCode>(c)),
            ["DigitalInSiemens"] = new(EquipTypeGroup.DI, c => SoeEventMapper.GetDescriptionOrEmpty<DiSoeCode>(c)),
            
            ["DigitalOut"] = new(EquipTypeGroup.DO, c => SoeEventMapper.GetDescriptionOrEmpty<DoSoeCode>(c)),
            ["DigitalOutSiemens"] = new(EquipTypeGroup.DO, c => SoeEventMapper.GetDescriptionOrEmpty<DoSoeCode>(c)),

            ["Motor"] = new(EquipTypeGroup.Motor, c => SoeEventMapper.GetDescriptionOrEmpty<MotorSoeCode>(c)),
            ["MotorSiemens"] = new(EquipTypeGroup.Motor, c => SoeEventMapper.GetDescriptionOrEmpty<MotorSoeCode>(c)),

            ["AnalogIn"] = new(EquipTypeGroup.AI, c => SoeEventMapper.GetDescriptionOrEmpty<AiSoeCode>(c)),
            ["AnalogInSiemens"] = new(EquipTypeGroup.AI, c => SoeEventMapper.GetDescriptionOrEmpty<AiSoeCode>(c)),
            
            ["AnalogInCalc"] = new(EquipTypeGroup.AI, c => SoeEventMapper.GetDescriptionOrEmpty<AiSoeCode>(c)),
            ["AnalogInCalcSiemens"] = new(EquipTypeGroup.AI, c => SoeEventMapper.GetDescriptionOrEmpty<AiSoeCode>(c)),

            ["ValveA"] = new(EquipTypeGroup.VGA, c => SoeEventMapper.GetDescriptionOrEmpty<AoSoeCode>(c)),
            ["ValveASiemens"] = new(EquipTypeGroup.VGA, c => SoeEventMapper.GetDescriptionOrEmpty<AoSoeCode>(c)),

            ["ValveA_EL"] = new(EquipTypeGroup.VGA_EL, c => SoeEventMapper.GetDescriptionOrEmpty<VgaElSoeCode>(c)),

            ["ValveD"] = new(EquipTypeGroup.VGD, c => SoeEventMapper.GetDescriptionOrEmpty<VgdSoeCode>(c)),
            ["ValveDSiemens"] = new(EquipTypeGroup.VGD, c => SoeEventMapper.GetDescriptionOrEmpty<VgdSoeCode>(c)),

            ["Atv"] = new(EquipTypeGroup.Atv, c => SoeEventMapper.GetDescriptionOrEmpty<AtvSoeCode>(c)),
            ["AtvSiemens"] = new(EquipTypeGroup.Atv, c => SoeEventMapper.GetDescriptionOrEmpty<AtvSoeCode>(c)),
        };

        public static EquipTypeGroup GetGroup(string? equipType)
        {
            var t = (equipType ?? "").Trim();
            return Map.TryGetValue(t, out var meta) ? meta.Group : EquipTypeGroup.All;
        }

        public static string GetEventText(string? equipType, int bitCode)
        {
            if (bitCode <= 0) return "";

            var t = (equipType ?? "").Trim();
            return Map.TryGetValue(t, out var meta) ? meta.EventText(bitCode) : "";
        }

        /// <summary>
        /// Поддерживается ли тип оборудования нашим приложением.
        /// Если false - не показываем equipment в левом списке.
        /// </summary>
        public static bool IsSupportedType(string? equipType)
        {
            var t = (equipType ?? "").Trim();
            return Map.ContainsKey(t);
        }

        #region Brush

        #region Type
        private static readonly IReadOnlyDictionary<EquipTypeGroup, Brush> _bg = new Dictionary<EquipTypeGroup, Brush>
        {
            { EquipTypeGroup.Equipment, MakeRgb(218, 226, 238) },
            { EquipTypeGroup.VGA,   MakeRgb(159, 223, 191) },
            { EquipTypeGroup.VGD,   MakeRgb(223, 191, 159) },
            { EquipTypeGroup.Motor, MakeRgb(229, 229, 229) },
            { EquipTypeGroup.AI,    MakeRgb(191, 223, 159) },
            { EquipTypeGroup.DI,    MakeRgb(170, 255, 255) },
            { EquipTypeGroup.DO,    MakeRgb(191, 211, 234) },
            { EquipTypeGroup.Atv,   MakeRgb(255, 213, 170) },
            { EquipTypeGroup.All,   Brushes.Transparent },
            { EquipTypeGroup.Favorites, Brushes.Transparent }
        };

        public static Brush GetGroupCellBrush(EquipTypeGroup group) =>
            _bg.TryGetValue(group, out var b) ? b : Brushes.Transparent;

        public static Brush GetTypeCellBrush(string rawType) =>
            GetGroupCellBrush(GetGroup(rawType));
        #endregion

        #region Event
        // пастельные фоны (можешь поменять)
        private static readonly Brush _evRed = MakeRgb(255, 199, 199);
        private static readonly Brush _evYellow = MakeRgb(255, 244, 179);
        private static readonly Brush _evGreen = MakeRgb(200, 230, 201);
        private static readonly Brush _evBlue = MakeRgb(187, 222, 251);
        private static readonly Brush _evLightBlue = MakeRgb(200, 240, 255);
        private static readonly Brush _evGray = MakeRgb(224, 224, 224);


        public static Brush GetEventCellBrush(string? eventKey)
        {
            var k = (eventKey ?? "").Trim();
            if (k.Length == 0) return Brushes.Transparent;

            // 1) серый — все Off / Down / Man
            if (k.EndsWith("_Off", StringComparison.OrdinalIgnoreCase) ||
                k.EndsWith("_Down", StringComparison.OrdinalIgnoreCase) ||
                k.EndsWith("_Man", StringComparison.OrdinalIgnoreCase))
                return _evGray;

            // 2) синий — все *_EN_On
            if (k.Contains("_EN_On", StringComparison.OrdinalIgnoreCase))
                return _evBlue;

            // 3) светло-синий — VALUE_TRUE_On / VALUE_FORCED_On / FORCE_CMD_On
            if (k.Equals("VALUE_TRUE_On", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("VALUE_FORCED_On", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("FORCE_CMD_On", StringComparison.OrdinalIgnoreCase))
                return _evLightBlue;

            // 4) зеленый — MODE_Auto / VALUE_On / CMD_On / MAN_On
            if (k.Equals("MODE_Auto", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("VALUE_On", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("CMD_On", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("MAN_On", StringComparison.OrdinalIgnoreCase))
                return _evGreen;

            // 5) желтый — AL_LW_Up / AL_HW_Up / AL_W_Up / T_WORK_AL_On
            if (k.Equals("AL_LW_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("AL_HW_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("AL_W_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("T_WORK_AL_On", StringComparison.OrdinalIgnoreCase))
                return _evYellow;

            // 6) красный — AL_LA_Up / AL_HA_Up / AL_A_Up / AL_HEALTH_Up / AL_On
            if (k.Equals("AL_LA_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("AL_HA_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("AL_A_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("AL_HEALTH_Up", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("AL_On", StringComparison.OrdinalIgnoreCase))
                return _evRed;

            return Brushes.Transparent;
        }
        #endregion

        private static SolidColorBrush MakeRgb(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        #endregion

    }
}
