using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Вкладки главного TabControl.
    /// Важно: значения должны совпадать с порядком вкладок в XAML (SelectedIndex).
    /// </summary>
    public enum MainTabKind
    {
        Param = 0,
        Info = 1,
        OperationActions = 2,
        AlarmHistory = 3,
        SOE = 4,
        Message = 5
    }
}
