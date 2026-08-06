namespace TechMES.Calc.Parameters;

/// <summary>
/// Определяет тип входного параметра расчёта.
///
/// В дальнейшем WEB и Maintenance будут выбирать редактор параметра
/// автоматически на основании этого значения.
/// </summary>
public enum CalculationParameterType
{
    Number,
    Integer,
    Boolean,
    Selection,
    Text
}

/// <summary>
/// Описывает одно доступное значение параметра типа Selection.
/// </summary>
/// <param name="Value">
/// Стабильное значение, передаваемое алгоритму.
/// </param>
/// <param name="Name">
/// Отображаемое пользователю название.
/// </param>
public sealed record CalculationParameterOption(string Value, string Name);

/// <summary>
/// Описывает один входной параметр расчёта.
///
/// Модель не привязана к конкретным параметрам Temperature, Pressure,
/// Level и поэтому позволяет добавлять новые входы без изменения
/// общей архитектуры WEB, Runtime и PostgreSQL.
/// </summary>
public sealed record CalculationParameterDefinition(
    string Key,
    string Name,
    CalculationParameterType Type,
    string? Unit = null,
    bool IsRequired = true,
    object? DefaultValue = null,
    double? Minimum = null,
    double? Maximum = null,
    double? Step = null,
    int Decimals = 2,
    int Order = 0,
    string? Description = null,
    IReadOnlyList<CalculationParameterOption>? Options = null);