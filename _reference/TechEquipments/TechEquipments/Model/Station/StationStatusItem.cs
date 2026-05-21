using TechEquipments.ViewModels;

namespace TechEquipments
{
    /// <summary>
    /// Один элемент Station ComboBox:
    /// - Name              : что показываем в UI
    /// - ProbeEquipmentName: какое equipment используем для проверки станции
    /// - ProbeTagName      : какой tag читаем для проверки
    /// - IsOffline         : рисовать ли offline-иконку
    /// </summary>
    public sealed class StationStatusItem : ObservableObject
    {
        public string Name { get; init; } = "";
        public string ProbeEquipmentName { get; init; } = "";
        public string ProbeTagName { get; init; } = "";

        private bool _isOffline;
        public bool IsOffline
        {
            get => _isOffline;
            set => SetProperty(ref _isOffline, value);
        }

        public override string ToString() => Name;
    }
}