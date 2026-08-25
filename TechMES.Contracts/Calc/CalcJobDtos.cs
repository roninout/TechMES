using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Источник фактического значения входного параметра расчёта.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcInputSourceTypeDto
{
    /// <summary>
    /// Значение читается из SCADA-тега через Runtime.Service.
    /// </summary>
    Tag,

    /// <summary>
    /// Постоянное значение хранится в конфигурации задания.
    /// </summary>
    Constant,

    /// <summary>
    /// Значение берётся из результата другого расчётного задания.
    /// Поддержка будет включена после реализации графа зависимостей.
    /// </summary>
    CalculationOutput
}

/// <summary>
/// Полное описание одного настроенного расчётного задания.
/// Формула находится в TechMES.Calc, а эта модель содержит только
/// эксплуатационные настройки и привязки.
/// </summary>
public sealed class CalcJobDto
{
    /// <summary>
    /// Идентификатор задания в PostgreSQL.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Необязательное оборудование, с которым связан расчёт.
    /// </summary>
    public string? EquipmentName { get; set; }

    /// <summary>
    /// Отображаемое название задания.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Дополнительное описание назначения расчёта.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Стабильный код алгоритма из TechMES.Calc.
    /// </summary>
    public string DefinitionCode { get; set; } = "";

    /// <summary>
    /// Ожидаемая версия математического поведения алгоритма.
    /// </summary>
    public string DefinitionVersion { get; set; } = "";

    /// <summary>
    /// Разрешено ли выполнять задание по расписанию.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Период выполнения в миллисекундах.
    /// </summary>
    public int PeriodMs { get; set; }

    /// <summary>
    /// Общее разрешение записи результатов в SCADA.
    /// По умолчанию запись всегда выключена.
    /// </summary>
    public bool WriteEnabled { get; set; }

    /// <summary>
    /// Порядок отображения и обработки задания.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Версия записи для защиты от одновременного редактирования
    /// через Maintenance и WEB.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// Время создания записи в UTC.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Время последнего изменения записи в UTC.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Привязки входных параметров.
    /// </summary>
    public List<CalcJobInputDto> Inputs { get; set; } = [];

    /// <summary>
    /// Привязки выходных значений.
    /// </summary>
    public List<CalcJobOutputDto> Outputs { get; set; } = [];
}

/// <summary>
/// Одна входная привязка сохранённого задания.
/// </summary>
public sealed class CalcJobInputDto
{
    /// <summary>
    /// Идентификатор привязки.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Ключ параметра из CalculationDefinition.
    /// </summary>
    public string ParameterKey { get; set; } = "";

    /// <summary>
    /// Тип источника значения.
    /// </summary>
    public CalcInputSourceTypeDto SourceType { get; set; }

    /// <summary>
    /// SCADA-тег для SourceType.Tag.
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Исходная пользовательская ссылка, из которой был разрешён TagName.
    /// Поле используется только для отображения и повторного редактирования конфигурации. Calc.Service всегда работает с уже разрешённым TagName.
    /// </summary>
    public string? SourceReference { get; set; }

    /// <summary>
    /// JSON-значение для SourceType.Constant.
    /// Поддерживает number, integer, boolean и string.
    /// </summary>
    public JsonElement? ConstantValue { get; set; }

    /// <summary>
    /// Исходное задание для SourceType.CalculationOutput.
    /// </summary>
    public long? SourceJobId { get; set; }

    /// <summary>
    /// Ключ выхода исходного задания.
    /// </summary>
    public string? SourceOutputKey { get; set; }

    /// <summary>
    /// Максимальный допустимый возраст значения SCADA-тега.
    /// Null означает использование общей настройки службы.
    /// </summary>
    public int? MaxAgeSeconds { get; set; }

    /// <summary>
    /// Порядок отображения входа.
    /// </summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Одна выходная привязка сохранённого задания.
/// </summary>
public sealed class CalcJobOutputDto
{
    /// <summary>
    /// Идентификатор привязки.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Ключ результата из CalculationDefinition.
    /// </summary>
    public string OutputKey { get; set; } = "";

    /// <summary>
    /// Целевой SCADA-тег.
    /// Может отсутствовать для shadow/read-only задания.
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Разрешена ли запись именно этого выхода.
    /// Также требуется общее CalcJob.WriteEnabled.
    /// </summary>
    public bool WriteEnabled { get; set; }

    /// <summary>
    /// Множитель перед записью результата.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Смещение после применения Scale.
    /// </summary>
    public double Offset { get; set; }

    /// <summary>
    /// Порядок отображения выхода.
    /// </summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Запрос создания или изменения задания.
/// Дочерние входы и выходы сохраняются как полный снимок.
/// </summary>
public sealed class CalcJobSaveRequest
{
    /// <summary>
    /// Оборудование, связанное с заданием.
    /// </summary>
    public string? EquipmentName { get; set; }

    /// <summary>
    /// Отображаемое название задания.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Дополнительное описание.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Код алгоритма из TechMES.Calc.
    /// </summary>
    public string DefinitionCode { get; set; } = "";

    /// <summary>
    /// Ожидаемая версия алгоритма.
    /// </summary>
    public string DefinitionVersion { get; set; } = "";

    /// <summary>
    /// Разрешение выполнения задания.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Период выполнения в миллисекундах.
    /// </summary>
    public int PeriodMs { get; set; } = 5000;

    /// <summary>
    /// Общее разрешение записи в SCADA.
    /// </summary>
    public bool WriteEnabled { get; set; }

    /// <summary>
    /// Порядок отображения и обработки.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Ожидаемая Revision при обновлении.
    /// Для нового задания должна быть null.
    /// </summary>
    public long? ExpectedRevision { get; set; }

    /// <summary>
    /// Полный набор входных привязок.
    /// </summary>
    public List<CalcJobInputSaveDto> Inputs { get; set; } = [];

    /// <summary>
    /// Полный набор выходных привязок.
    /// </summary>
    public List<CalcJobOutputSaveDto> Outputs { get; set; } = [];
}

/// <summary>
/// Входная привязка в запросе сохранения.
/// </summary>
public sealed class CalcJobInputSaveDto
{
    public string ParameterKey { get; set; } = "";

    public CalcInputSourceTypeDto SourceType { get; set; }

    /// <summary>
    /// Реальный Plant SCADA Variable Tag, который будет читать Calc.Service.
    /// </summary>
    public string? TagName { get; set; }

    /// <summary>
    /// Исходная ссылка, которую пользователь настроил в WEB.
    /// </summary>
    public string? SourceReference { get; set; }

    public JsonElement? ConstantValue { get; set; }

    public long? SourceJobId { get; set; }

    public string? SourceOutputKey { get; set; }

    public int? MaxAgeSeconds { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Выходная привязка в запросе сохранения.
/// </summary>
public sealed class CalcJobOutputSaveDto
{
    public string OutputKey { get; set; } = "";
    public string? TagName { get; set; }
    public bool WriteEnabled { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Ответ со списком настроенных расчётных заданий.
/// </summary>
public sealed class CalcJobsResponse
{
    public List<CalcJobDto> Items { get; set; } = [];
}

/// <summary>
/// Ответ HTTP 409 при конфликте одновременного редактирования.
/// </summary>
public sealed class CalcJobRevisionConflictResponse
{
    public string ErrorCode { get; set; } = "job.revision-conflict";
    public string ErrorMessage { get; set; } = "";
    public long JobId { get; set; }
    public long ExpectedRevision { get; set; }
    public long CurrentRevision { get; set; }
}