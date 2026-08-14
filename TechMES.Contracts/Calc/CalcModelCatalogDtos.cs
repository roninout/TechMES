using System.Text.Json.Serialization;

namespace TechMES.Contracts.Calc;

/// <summary>
/// Тип SCADA calculation model.
/// Значение соответствует Equipment Type в Plant SCADA.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalcModelTypeDto
{
    Tank,
    Density,
    Capacity,
    Content
}

/// <summary>
/// Один calculation model, обнаруженный в Plant SCADA.
///
/// Inputs, Constants, Outputs и алгоритм хранятся отдельно в Calc Job.
///
/// TagNames содержит реальные variable tags, которые уже были найдены
/// во время Calc Catalog scan. Это позволяет специализированному UI
/// автоматически создавать bindings без повторного CtApi discovery.
/// </summary>
public sealed class CalcModelDto
{
    /// <summary>
    /// Полное Equipment Name.
    /// Например: S12.T06.LC01.Tank.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// COMMENT оборудования из Plant SCADA.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Станция, извлечённая из первого сегмента Equipment Name.
    /// Например: S12.
    /// </summary>
    public string Station { get; set; } = "";

    /// <summary>
    /// Реальный Equipment Type Plant SCADA.
    /// </summary>
    public CalcModelTypeDto Type { get; set; }

    /// <summary>
    /// Реальные variable tags данного Calc Equipment,
    /// найденные во время Calc Catalog scan.
    ///
    /// Например для Tank:
    /// *_H_MAX
    /// *_H_HMI
    /// *_V_HMI
    /// *_M_HMI
    ///
    /// Для Density здесь же находится его *_HMI tag.
    /// </summary>
    public IReadOnlyList<string> TagNames { get; set; } = [];
}

/// <summary>
/// Текущее содержимое in-memory Calc Catalog Runtime.Service.
/// </summary>
public sealed class CalcModelCatalogResponse
{
    /// <summary>
    /// Доступен ли provider для текущей конфигурации Runtime.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Был ли каталог хотя бы один раз успешно загружен через CtApi.
    /// </summary>
    public bool IsLoaded { get; set; }

    /// <summary>
    /// Время последней успешной загрузки.
    /// </summary>
    public DateTimeOffset? LoadedAtUtc { get; set; }

    public int TotalCount { get; set; }

    public IReadOnlyList<CalcModelDto> Items { get; set; } = [];

    /// <summary>
    /// Только станции, на которых существуют Calc models.
    /// </summary>
    public IReadOnlyList<string> Stations { get; set; } = [];

    /// <summary>
    /// Только реально найденные типы.
    /// </summary>
    public IReadOnlyList<CalcModelTypeDto> Types { get; set; } = [];

    public string? ErrorMessage { get; set; }
}