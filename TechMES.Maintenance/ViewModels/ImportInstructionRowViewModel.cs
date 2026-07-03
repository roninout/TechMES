namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Editable INSTRUCTION row. Runtime provides Station/Type/Equipment, while the operator links a product code and source file.
/// </summary>
public sealed class ImportInstructionRowViewModel : ObservableObject
{
    private string _station = "";
    private string _type = "";
    private string _equipment = "";
    private string _productCode = "";
    private string _source = "";
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

    public string ProductCode
    {
        get => _productCode;
        set => SetProperty(ref _productCode, value);
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
