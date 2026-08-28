using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Тип входного параметра расчёта, передаваемый клиентам Runtime.
///
/// DTO enum отделён от внутреннего CalculationParameterType,
/// чтобы WEB и Maintenance не зависели от проекта TechMES.Calc.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcParameterTypeDto
{
    Number,
    Integer,
    Boolean,
    Selection,
    Text
}

/// <summary>
/// Назначение входного параметра Calculation Definition.
/// Configuration: настройка алгоритма, обычно сохраняемая как Constant.
/// ProcessInput:
/// фактическое процессное значение, которое специализированный UI должен позволять связать с SCADA Tag или другим Runtime source.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcParameterRoleDto
{
    Configuration,
    ProcessInput
}

/// <summary>
/// Один доступный вариант параметра типа Selection.
/// </summary>
public sealed class CalcParameterOptionDto
{
    /// <summary>
    /// Стабильное значение, передаваемое алгоритму.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Отображаемое пользователю название.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Необязательная физическая фаза варианта.
    /// Для Substance Options:
    ///     liquid
    ///     vapor
    /// Для остальных Selection-параметров null.
    /// Поле предназначено для логики UI и не зависит от пользовательского отображаемого Name.
    /// </summary>
    public string? Phase { get; set; }
}

/// <summary>
/// Описание одного входного параметра алгоритма.
///
/// По этой модели WEB сможет динамически создавать Numeric input,
/// CheckBox, DropDown или TextBox без знания конкретной формулы.
/// </summary>
public sealed class CalcParameterDefinitionDto
{
    /// <summary>
    /// Стабильный ключ параметра, например levelMm или pressure.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Отображаемое название параметра.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Тип редактора и передаваемого значения.
    /// </summary>
    public CalcParameterTypeDto Type { get; set; }


    /// <summary>
    /// Назначение параметра внутри алгоритма.
    ///
    /// Специализированные панели Density/Capacity используют это поле,
    /// чтобы автоматически отделить процессные bindings от постоянных настроек расчёта.
    /// </summary>
    public CalcParameterRoleDto Role { get; set; }

    /// <summary>
    /// Необязательный стабильный SubstanceCode, к которому относится данный Configuration parameter.
    /// Null означает, что параметр относится к Calculation в целом.
    /// Например: DryMatter -> Purity.
    /// </summary>
    public string? AppliesToSubstanceCode { get; set; }

    /// <summary>
    /// Единица измерения, если она применима.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Признак обязательного параметра.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Значение по умолчанию в JSON-представлении.
    ///
    /// JsonElement позволяет сохранить number, boolean, string или null
    /// без добавления отдельных свойств для каждого возможного типа.
    /// </summary>
    public JsonElement? DefaultValue { get; set; }

    /// <summary>
    /// Минимальное допустимое числовое значение.
    /// </summary>
    public double? Minimum { get; set; }

    /// <summary>
    /// Максимальное допустимое числовое значение.
    /// </summary>
    public double? Maximum { get; set; }

    /// <summary>
    /// Рекомендуемый шаг изменения числового значения.
    /// </summary>
    public double? Step { get; set; }

    /// <summary>
    /// Рекомендуемое количество десятичных знаков.
    /// </summary>
    public int Decimals { get; set; }

    /// <summary>
    /// Порядок отображения параметра.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Дополнительное описание параметра.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Доступные варианты для параметра Selection.
    /// </summary>
    public List<CalcParameterOptionDto> Options { get; set; } = [];
}

/// <summary>
/// Описание одного выходного значения алгоритма.
/// </summary>
public sealed class CalcOutputDefinitionDto
{
    /// <summary>
    /// Стабильный ключ результата, например volume или density.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Отображаемое название результата.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Единица измерения результата.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Рекомендуемое количество десятичных знаков.
    /// </summary>
    public int Decimals { get; set; }

    /// <summary>
    /// Порядок отображения результата.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Дополнительное описание результата.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Полное транспортное описание одного доступного алгоритма.
/// </summary>
public sealed class CalcDefinitionDto
{
    /// <summary>
    /// Стабильный код алгоритма.
    /// </summary>
    public string Code { get; set; } = "";

    /// <summary>
    /// Отображаемое название.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Категория для группировки в WEB и Maintenance.
    /// </summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// Версия математического поведения алгоритма.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// Входные параметры алгоритма.
    /// </summary>
    public List<CalcParameterDefinitionDto> Parameters { get; set; } = [];

    /// <summary>
    /// Выходные значения алгоритма.
    /// </summary>
    public List<CalcOutputDefinitionDto> Outputs { get; set; } = [];
}

/// <summary>
/// Ответ со всеми алгоритмами, встроенными в установленную версию Runtime.
/// </summary>
public sealed class CalcDefinitionsResponse
{
    /// <summary>
    /// Доступные алгоритмы.
    /// </summary>
    public List<CalcDefinitionDto> Items { get; set; } = [];
}