namespace TechMES.Web.Components.Pages;

/// <summary>Восстановление выбора выполняется до загрузки каталога, без обратного сохранения.</summary>
public partial class Equipment
{
    // Числа соответствуют старому EquipTypeGroup, а не WEB EquipmentTypeGroup.
    private static readonly string[] SavedTypeNames = ["All", "Equipment", "VGA", "VGA_EL", "VGD", "Motor", "AI", "DI", "DO", "ATV", "Favorites"];
    private string _activeFilterKey = "";

    private bool RestoreUserState()
    {
        var state = UserState.Value;
        var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var pages = new[] { "/equipment", "/equipment/param", "/events", "/alarm-history", "/alarms", "/soe", "/messages", "/recipes", "/calculations" };
        var oldPage = state.SelectedTab switch { 0 => "/equipment/param", 2 => "/events", 3 => "/alarm-history", 4 => "/soe", 5 => "/messages", _ => "/equipment" };
        var page = pages.Contains(state.LastPage, StringComparer.OrdinalIgnoreCase) ? state.LastPage : oldPage;

        // Явная ссылка имеет приоритет, как ExternalTag в исходном приложении.
        if (relative.Length == 0 && page != "/equipment")
        {
            NavigationManager.NavigateTo(page, replace: true);
            return false;
        }

        _selectedStation = string.IsNullOrWhiteSpace(state.SelectedStation) || state.SelectedStation == "All" ? AllStationsText : state.SelectedStation;
        _selectedType = state.SelectedTypeFilter is > 0 and < 11 ? SavedTypeNames[state.SelectedTypeFilter] : AllTypesText;
        _activeFilterKey = UserFilterKey();

        return true;
    }

    private string UserFilterKey() => $"{(_selectedStation == AllStationsText ? "All" : _selectedStation)}|{(_selectedType == AllTypesText ? "All" : _selectedType)}";

    private string? SavedEquipmentForFilter()
    {
        return UserState.Value.LastEquipmentsByFilter.FirstOrDefault(pair => pair.Key.Equals(UserFilterKey(), StringComparison.OrdinalIgnoreCase)).Value;
    }

    private string? SavedEquipmentName() => SavedEquipmentForFilter() ?? UserState.Value.LastEquipName;

    private string? SavedEquipmentNodeId() => string.Equals(SavedEquipmentName(), UserState.Value.LastEquipName, StringComparison.OrdinalIgnoreCase) ? UserState.Value.LastNodeId : null;

    private void RememberUserSelection()
    {
        var key = UserFilterKey();
        _activeFilterKey = key;

        // При загрузке/восстановлении не записываем автоматически выбранную строку.
        if (_isLoading || !UserState.IsLoaded) return;

        var type = Array.FindIndex(SavedTypeNames, value => value.Equals(_selectedType, StringComparison.OrdinalIgnoreCase));
        var remembered = new Dictionary<string, string>();

        if (_selectedEquipment is not null)
            remembered[key] = _selectedEquipment.Name;

        UserState.Update(new
        {
            LastEquipName = _selectedEquipment?.Name ?? UserState.Value.LastEquipName,
            LastNodeId = _selectedEquipment?.NodeId ?? UserState.Value.LastNodeId,
            SelectedStation = _selectedStation == AllStationsText ? "All" : _selectedStation,
            SelectedTypeFilter = Math.Max(0, type),
            LastEquipmentsByFilter = remembered
        });
    }
}