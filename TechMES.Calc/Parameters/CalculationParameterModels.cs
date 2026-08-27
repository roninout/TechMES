namespace TechMES.Calc.Parameters;

/// <summary>
/// Определяет тип значения входного параметра расчёта.
///
/// Тип описывает именно формат данных:
/// Number, Integer, Boolean, Selection или Text.
///
/// Он не определяет назначение параметра.
/// Для назначения используется CalculationParameterRole.
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
/// Определяет назначение параметра внутри Calculation Definition.
///
/// Configuration:
/// значение является настройкой самого расчёта.
/// Например:
/// - количество компонентов смеси;
/// - выбранное вещество;
/// - процент вещества;
/// - геометрический размер Tank;
/// - correction.
///
/// Такие значения обычно сохраняются как ConstantValue Calc Job.
///
/// ProcessInput:
/// фактическое значение процесса, которое должно поступать
/// в расчёт при каждом рабочем цикле.
///
/// Например:
/// - Temperature;
/// - Pressure;
/// - в будущем Humidity;
/// - Concentration;
/// - Compressibility;
/// - любой другой измеряемый параметр.
///
/// Количество ProcessInput нигде не ограничено.
/// Специализированный WEB UI может динамически строить
/// строки привязки на основании этого признака.
/// </summary>
public enum CalculationParameterRole
{
    Configuration,
    ProcessInput
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
/// <param name="Phase">
/// Необязательная физическая фаза варианта.
///
/// Поле используется для Selection-параметров компонентов смеси.
/// Для веществ содержит нормализованное значение:
///
///     liquid
///     vapor
///
/// Для обычных Selection, не связанных с веществами, остаётся null.
///
/// Phase хранится отдельно от Name намеренно:
/// пользовательское отображаемое название может изменяться,
/// а логика WEB не должна анализировать его текст.
/// </param>
public sealed record CalculationParameterOption(string Value, string Name, string? Phase = null);

/// <summary>
/// Полностью описывает один входной параметр Calculation Definition.
///
/// Модель намеренно не привязана к конкретным Temperature,
/// Pressure, Level или другим известным сегодня параметрам.
///
/// Благодаря этому новый алгоритм может объявлять любое количество
/// собственных входов без изменения общей архитектуры Calc.
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
    IReadOnlyList<CalculationParameterOption>? Options = null,
    CalculationParameterRole Role = CalculationParameterRole.Configuration);