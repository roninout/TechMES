namespace TechMES.Contracts.Recipes;

/// <summary>Операции с существующей стадией сохранённого рецепта.</summary>
public enum RecipeStageChangeOperation
{
    MoveUp = 1,
    MoveDown = 2,
    Delete = 3,
    SetAuto = 4,
    MoveTo = 5
}

/// <summary>Ожидаемое состояние строки; значения параметров сюда не входят.</summary>
public sealed class RecipeStagePositionDto
{
    public int Id { get; set; }
    public int Number { get; set; }
    public bool Auto { get; set; }
}

/// <summary>Передаёт операцию и ожидаемый порядок для защиты от устаревшей таблицы.</summary>
public sealed class RecipeStageChangeRequest
{
    public RecipeStageChangeOperation Operation { get; set; }
    public int StageId { get; set; }
    public bool? Auto { get; set; }

    /// <summary>Новая позиция MoveTo, начиная с нуля; номера в БД остаются 1..N.</summary>
    public int? TargetIndex { get; set; }

    public List<RecipeStagePositionDto> ExpectedStages { get; set; } = [];
    public string? Actor { get; set; }
    public List<string> ActorGroups { get; set; } = [];
}

/// <summary>Результат операции и строка для выделения после перечитывания.</summary>
public sealed class RecipeStageChangeResult
{
    public RecipeChangeStatus Status { get; set; }
    public int? SelectedStageId { get; set; }

    /// <summary>Фактически назначенный prefix при добавлении стадии.</summary>
    public string? TagPrefix { get; set; }

    public string? Error { get; set; }
}