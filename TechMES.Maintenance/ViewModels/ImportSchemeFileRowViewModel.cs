namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Editable SCHEME library row. It describes a scheme file before equipment links are created.
/// </summary>
public sealed class ImportSchemeFileRowViewModel : ObservableObject
{
    private string _type = "";
    private string _source = "";
    private string _description = "";

    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }
}
