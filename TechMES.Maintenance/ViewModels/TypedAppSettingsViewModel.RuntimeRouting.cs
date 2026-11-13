namespace TechMES.Maintenance.ViewModels;

public sealed partial class TypedAppSettingsViewModel
{
    private string _webRuntimeSecondaryBaseUrl = "";

    // Второй адрес Runtime для WEB; пустое значение отключает второй адрес.
    public string WebRuntimeSecondaryBaseUrl
    {
        get => _webRuntimeSecondaryBaseUrl;
        set => SetProperty(ref _webRuntimeSecondaryBaseUrl, value);
    }
}