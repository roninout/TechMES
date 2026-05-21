using System;
using System.Collections.Generic;

namespace TechEquipments
{
    /// <summary>
    /// Состояние UI, которое сохраняем между запусками приложения.
    /// </summary>
    public sealed class UserState
    {
        /// <summary>Последнее введённое/использованное имя оборудования (поиск).</summary>
        public string LastEquipName { get; set; } = "";

        /// <summary>Последняя выбранная дата для DB-вкладок.</summary>
        public DateTime DbDate { get; set; } = DateTime.Today;

        /// <summary>Последняя активная вкладка (SOE/Operation actions/Alarm history).</summary>
        public MainTabKind SelectedTab { get; set; } = MainTabKind.SOE;

        /// <summary>Фильтр Station (левая панель).</summary>
        public string SelectedStation { get; set; } = "All";

        /// <summary>Фильтр Type (левая панель).</summary>
        public EquipTypeGroup SelectedTypeFilter { get; set; } = EquipTypeGroup.All;

        /// <summary>
        /// Память последнего выбранного оборудования для пары фильтров:
        /// ключ = "Station|TypeGroup", значение = EquipName.
        /// </summary>
        public Dictionary<string, string> LastEquipmentsByFilter { get; set; } = new();

        /// <summary>
        /// Последняя выбранная камера для QR-сканера.
        /// </summary>
        public int QrCameraIndex { get; set; } = 0;
    }
}