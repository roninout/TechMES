namespace TechMES.Web.Components.Calc.Tanks;

/// <summary>
/// Единый источник пользовательских описаний Tank-параметров.
///
/// Эти тексты используются:
/// - в интерактивных Preview Type 1..8 как tooltip;
/// - в TankConfigurationPanel;
/// - при необходимости позже их можно использовать и в Help.
///
/// Благодаря этому смысл dimA..dimG не дублируется
/// в восьми отдельных Razor-компонентах.
/// </summary>
internal static class TankFieldDescriptions
{
    public static string Get(int typeNumber, string key)
    {
        return key.ToLowerInvariant() switch
        {
            "tanktype" =>
                "Select the physical Tank geometry used for volume calculation.",

            "enabled" =>
                "Enable or disable execution of this calculation Job.",


            // ============================================================
            // Geometry
            // ============================================================

            "dima" => typeNumber switch
            {
                1 => "Height of cylindrical part.",
                2 => "Length of cylindrical part.",
                3 => "Height of cylindrical part.",
                4 => "Height of tank body.",
                5 => "Height of cylindrical part.",
                6 => "Height of cylindrical part.",
                7 => "Length of cylindrical part.",
                8 => "Height of cylindrical part.",
                _ => "Main Tank dimension."
            },

            "dimb" => typeNumber switch
            {
                1 => "Tank diameter.",
                2 => "Tank diameter.",
                3 => "Tank diameter.",
                4 => "Tank width.",
                5 => "Tank diameter.",
                6 => "Tank diameter.",
                7 => "Tank diameter.",
                8 => "Tank diameter.",
                _ => "Tank width / diameter."
            },

            "dimc" => typeNumber switch
            {
                1 => "Elliptical head height.",
                2 => "Elliptical end depth.",
                3 => "Lower elliptical head height.",
                4 => "Bottom depth.",
                5 => "Lower elliptical head height.",
                6 => "Height of each conical / frustum head.",
                7 => "Axial length of each conical / frustum end.",
                8 => "Lower conical / frustum head height.",
                _ => "Additional Tank dimension."
            },

            "dimd" => typeNumber switch
            {
                3 => "Partition distance from the left wall.",
                5 => "Tube bundle height.",
                6 => "Small end diameter of the upper and lower conical / frustum heads.",
                7 => "Small end diameter of both conical / frustum ends.",
                8 => "Tube bundle height.",
                _ => "Additional section dimension."
            },

            "dime" => typeNumber switch
            {
                5 => "Distance from the top of the cylindrical part to the top of the tube bundle.",
                8 => "Distance from the top of the cylindrical part to the top of the tube bundle.",
                _ => "Upper section dimension."
            },

            "dimf" => typeNumber switch
            {
                5 => "Total liquid volume displaced by the reboiler tube bundle.",
                8 => "Total liquid volume displaced by the reboiler tube bundle.",
                _ => "Additional volume parameter."
            },

            "dimg" => typeNumber switch
            {
                8 => "Diameter of the small bottom base of the conical / frustum head. 0 mm creates a true cone; dimG = dimB creates a cylindrical section.",
                _ => "Additional Tank dimension."
            },


            // ============================================================
            // Sensor
            // ============================================================

            "upperdeadarea" =>
                "Unmeasured area above the sensor working range.",

            "measurementarea" =>
                "Calculated sensor working range: total Tank height minus the upper and lower dead areas.",

            "lowerdeadarea" =>
                "Unmeasured area below the sensor working range.",

            "calculateabove100" =>
                "Continue volume calculation above 100% inside the upper dead area. The displayed Level itself is not limited to 100%.",

            _ => key
        };
    }
}