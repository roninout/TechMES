namespace TechMES.Maintenance.ViewModels;

/// <summary>
/// Editable SCHEME row.
/// One row describes one scheme file and its logical targets:
/// Station, Group and/or Equipment.
/// </summary>
public sealed class ImportSchemeFileRowViewModel : ObservableObject
{
    private long? _id;
    private string _type = "";
    private string _source = "";
    private string _description = "";
    private string _station = "";
    private string _groupNames = "";
    private string _equipments = "";
    private string _fileHash = "";
    private string? _pendingSourceFilePath;

    /// <summary>
    /// Existing public.equip_scheme row ID.
    /// Null means that the row has not been stored yet.
    /// </summary>
    public long? Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

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
    /// Scheme file name displayed in the grid and stored in
    /// public.equip_scheme.file_name.
    ///
    /// This property must not be used to preserve the physical Browse path.
    /// </summary>
    public string Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    /// <summary>
    /// SHA-256 stored in public.equip_scheme.file_hash.
    /// Used to detect duplicate files before Save.
    /// </summary>
    public string FileHash
    {
        get => _fileHash;
        set => SetProperty(ref _fileHash, value);
    }

    /// <summary>
    /// Full physical path selected through Browse.
    /// It exists only until Save and is not stored in PostgreSQL.
    /// </summary>
    public string? PendingSourceFilePath
    {
        get => _pendingSourceFilePath;
        set => SetProperty(ref _pendingSourceFilePath, value);
    }

    /// <summary>
    /// Legacy display name/description.
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// Station targets. Several values can be separated with comma or semicolon.
    /// </summary>
    public string Station
    {
        get => _station;
        set => SetProperty(ref _station, value);
    }

    /// <summary>
    /// Group targets. Several values can be separated with comma or semicolon.
    /// </summary>
    public string GroupNames
    {
        get => _groupNames;
        set => SetProperty(ref _groupNames, value);
    }

    /// <summary>
    /// Equipment targets. Several values can be separated with comma or semicolon.
    /// </summary>
    public string Equipments
    {
        get => _equipments;
        set => SetProperty(ref _equipments, value);
    }
}