namespace TechMES.Contracts.Recipes;

/// <summary>
/// Операции с уже существующей стадией сохранённого рецепта.
/// </summary>
public enum RecipeStageChangeOperation
{
    MoveUp = 1,
    MoveDown = 2,
    Delete = 3,
    SetAuto = 4
}

/// <summary>
/// Состояние строки на момент отправки операции; значения параметров сюда не входят.
/// </summary>
public sealed class RecipeStagePositionDto
{
    public int Id { get; set; }
    public int Number { get; set; }
    public bool Auto { get; set; }
}

/// <summary>
/// Передаёт ожидаемый порядок стадий для защиты от устаревшего состояния таблицы.
/// </summary>
public sealed class RecipeStageChangeRequest
{
    public RecipeStageChangeOperation Operation { get; set; }
    public int StageId { get; set; }
    public bool? Auto { get; set; }
    public List<RecipeStagePositionDto> ExpectedStages { get; set; } = [];
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

/// <summary>
/// Возвращает результат и ID строки, которую нужно выделить после перечитывания.
/// </summary>
public sealed class RecipeStageChangeResult
{
    public RecipeChangeStatus Status { get; set; }
    public int? SelectedStageId { get; set; }
    public string? Error { get; set; }
}