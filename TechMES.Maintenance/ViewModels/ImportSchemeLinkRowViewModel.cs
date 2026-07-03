namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Editable SCHEME link row. It connects one Runtime equipment item to one scheme file.
/// </summary>
public sealed class ImportSchemeLinkRowViewModel : ObservableObject
{
    private string _station = "";
    private string _type = "";
    private string _equipment = "";
    private string _scheme = "";
    private string _description = "";

    public string Station
    {
        get => _station;
        set => SetProperty(ref _station, value);
    }

    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public string Equipment
    {
        get => _equipment;
        set => SetProperty(ref _equipment, value);
    }

    public string Scheme
    {
        get => _scheme;
        set => SetProperty(ref _scheme, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }
}
