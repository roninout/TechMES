namespace TechMES.Contracts.Recipes;

/// <summary>
/// Тип редактора параметра. Значения совместимы с TecxRecipeAX.
/// </summary>
public enum RecipeParameterKind
{
    InputField = 0,
    CheckBox = 1
}

/// <summary>
/// Формат числового значения. Значения совместимы с TecxRecipeAX.
/// </summary>
public enum RecipeParameterFormat
{
    Default = 0,
    N1 = 1,
    N2 = 2,
    N3 = 3,
    N4 = 4
}

/// <summary>
/// Каталог привязок объектов и доступных рецептов.
/// </summary>
public sealed class RecipeCatalogDto
{
    public List<RecipePageDto> Pages { get; set; } = [];
    public List<RecipeListItemDto> Recipes { get; set; } = [];
}

/// <summary>
/// Привязка страницы SCADA к текущему и базовому рецептам.
/// </summary>
public sealed class RecipePageDto
{
    public int Id { get; set; }
    public string Page { get; set; } = "";
    public int RecipeId { get; set; }
    public int BaseRecipeId { get; set; }
}

/// <summary>
/// Заголовок рецепта без загрузки его стадий.
/// </summary>
public sealed class RecipeListItemDto
{
    public int Id { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public int? BaseRecipeId { get; set; }

    public string DisplayName => $"[{Id}] {Name}";
}

/// <summary>
/// Снимок одного рецепта: динамические колонки и упорядоченные стадии.
/// </summary>
public sealed class RecipeSnapshotDto
{
    public RecipeListItemDto Recipe { get; set; } = new();
    public List<RecipeColumnDto> Columns { get; set; } = [];
    public List<RecipeStageDto> Stages { get; set; } = [];
}

/// <summary>
/// Метаданные динамической колонки из BaseParameter.
/// </summary>
public sealed class RecipeColumnDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Order { get; set; }
    public string TagSuffix { get; set; } = "";
    public RecipeParameterKind Kind { get; set; }
    public RecipeParameterFormat Format { get; set; }
}

/// <summary>
/// Стадия рецепта. Ключ Parameters — ID базового параметра, а не его имя.
/// </summary>
public sealed class RecipeStageDto
{
    public int Id { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public bool Auto { get; set; }
    public int BaseStageId { get; set; }
    public string TagPrefix { get; set; } = "";
    public Dictionary<int, RecipeParameterDto> Parameters { get; set; } = [];
}

/// <summary>
/// Значение параметра конкретной стадии.
/// Active переносится из БД без изменения смысла старого поля.
/// </summary>
public sealed class RecipeParameterDto
{
    public int Id { get; set; }
    public double Value { get; set; }
    public bool Active { get; set; }
}