using System.Text.Json;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Read-only снимок конфигурации для TechMES.Calc.Service.
///
/// Snapshot содержит только enabled-задания, успешно проверенные
/// Runtime.Service по установленному каталогу алгоритмов.
/// </summary>
public sealed class CalcConfigurationSnapshotDto
{
    /// <summary>
    /// Стабильная версия снимка.
    ///
    /// Значение изменяется при создании, удалении или изменении
    /// любого enabled-задания.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// Время формирования снимка в UTC.
    /// </summary>
    public DateTimeOffset GeneratedAtUtc { get; set; }

    /// <summary>
    /// Общее количество enabled-заданий до проверки.
    /// </summary>
    public int EnabledJobCount { get; set; }

    /// <summary>
    /// Задания, готовые для Calc.Service.
    /// </summary>
    public List<CalcExecutionJobDto> Jobs { get; set; } = [];

    /// <summary>
    /// Enabled-задания, отклонённые Runtime из-за неправильной
    /// конфигурации или несовместимой версии алгоритма.
    /// </summary>
    public List<CalcConfigurationIssueDto> Issues { get; set; } = [];
}

/// <summary>
/// Подготовленное задание для выполнения Calc.Service.
/// </summary>
public sealed class CalcExecutionJobDto
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public string Name { get; set; } = "";
    public string? EquipmentName { get; set; }
    public string DefinitionCode { get; set; } = "";
    public string DefinitionVersion { get; set; } = "";
    public int PeriodMs { get; set; }
    public int SortOrder { get; set; }

    /// <summary>
    /// Общее разрешение записи. На текущем этапе всегда false.
    /// </summary>
    public bool WriteEnabled { get; set; }

    public List<CalcExecutionInputDto> Inputs { get; set; } = [];
    public List<CalcExecutionOutputDto> Outputs { get; set; } = [];
}

/// <summary>
/// Подготовленная входная привязка.
/// </summary>
public sealed class CalcExecutionInputDto
{
    public string ParameterKey { get; set; } = "";
    public CalcInputSourceTypeDto SourceType { get; set; }
    public string? TagName { get; set; }
    public JsonElement? ConstantValue { get; set; }
    public int? MaxAgeSeconds { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Подготовленная выходная привязка.
/// </summary>
public sealed class CalcExecutionOutputDto
{
    public string OutputKey { get; set; } = "";
    public string? TagName { get; set; }
    public bool WriteEnabled { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Причина, по которой enabled-задание не вошло в snapshot.
/// </summary>
public sealed class CalcConfigurationIssueDto
{
    public long JobId { get; set; }
    public string JobName { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}