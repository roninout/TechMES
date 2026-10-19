namespace TechMES.Contracts.Recipes;

/// <summary>
/// Сохранение одной ячейки.
/// ExpectedValue защищает от перезаписи изменений другого клиента.
/// </summary>
public sealed class RecipeParameterSaveRequest
{
    public int ParameterId { get; set; }
    public int StageId { get; set; }
    public int BaseParameterId { get; set; }
    public double? ExpectedValue { get; set; }
    public double? Value { get; set; }
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

/// <summary>
/// Результат проверки и сохранения параметра в хранилище.
/// </summary>
public enum RecipeParameterSaveStatus
{
    Unknown = 0,
    Saved = 1,
    NotFound = 2,
    Conflict = 3,
    InvalidValue = 4
}

/// <summary>
/// Value возвращается после успешного сохранения.
/// </summary>
public sealed class RecipeParameterSaveResult
{
    public RecipeParameterSaveStatus Status { get; set; }
    public double? Value { get; set; }
    public string? Error { get; set; }
}