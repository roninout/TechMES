namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Editable SCHEME row.
/// One row describes one scheme file and its logical targets:
/// Station, Group and/or Equipment.
/// </summary>
public sealed class ImportSchemeFileRowViewModel : ObservableObject
{
    private string _type = "";
    private string _source = "";
    private string _description = "";
    private string _station = "";
    private string _groupNames = "";
    private string _equipments = "";

    /// <summary>
    /// Legacy/import type. It is kept for compatibility with existing import code,
    /// but it is no longer displayed on the SCHEME tab.
    /// </summary>
    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    /// <summary>
    /// Scheme file source/name.
    /// Stored in public.equip_scheme.file_name after Save.
    /// </summary>
    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    /// <summary>
    /// Legacy display name/description.
    /// It is kept for compatibility, but it is no longer displayed on the SCHEME tab.
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// Station targets. Several values can be entered with comma or semicolon.
    /// Stored in public.equip_scheme.station.
    /// </summary>
    public string Station
    {
        get => _station;
        set => SetProperty(ref _station, value);
    }

    /// <summary>
    /// Group targets. Several values can be entered with comma or semicolon.
    /// Stored in public.equip_scheme.group_names.
    /// </summary>
    public string GroupNames
    {
        get => _groupNames;
        set => SetProperty(ref _groupNames, value);
    }

    /// <summary>
    /// Equipment targets. Several values can be entered with comma or semicolon.
    /// Stored in public.equip_scheme.equipments.
    /// </summary>
    public string Equipments
    {
        get => _equipments;
        set => SetProperty(ref _equipments, value);
    }
}