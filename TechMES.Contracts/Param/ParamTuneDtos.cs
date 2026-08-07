using TechMES.Contracts.Equipment;

namespace TechMES.Contracts.Param;

/// <summary>
/// Хранимые настройки PID Tune для одного VGA-оборудования.
/// </summary>
public sealed class ParamTuneSettingsResponse
{
    public string EquipmentName { get; set; } = "";

    public string? Pv { get; set; }

    public double? PvMin { get; set; }

    public double? PvMax { get; set; }

    public bool PvTrendFound { get; set; }

    public string? Sp { get; set; }

    public double? SpMin { get; set; }

    public double? SpMax { get; set; }

    public bool SpTrendFound { get; set; }

    /// <summary>
    /// Online-тег фактического Kp, с которым выполняется ClosedLoop-тест.
    /// Для него требуется только числовой TagRead; trend-reference не нужен.
    /// </summary>
    public string? TestKpTag { get; set; }

    /// <summary>
    /// true, если TestKpTag успешно прочитан как числовой online-тег.
    /// </summary>
    public bool TestKpFound { get; set; }

    /// <summary>
    /// Последний рассчитанный коэффициент Kp, сохранённый оператором.
    /// Не путать с TestKpTag: TestKpTag нужен только для определения Ku.
    /// </summary>
    public double? Kp { get; set; }

    public double? Ti { get; set; }

    public double? Td { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Запрос сохранения PID Tune-настроек.
/// </summary>
public sealed class ParamTuneSaveRequest
{
    public string? Pv { get; set; }

    public double? PvMin { get; set; }

    public double? PvMax { get; set; }

    public string? Sp { get; set; }

    public double? SpMin { get; set; }

    public double? SpMax { get; set; }

    /// <summary>
    /// Online-тег фактического Kp для ClosedLoop-идентификации.
    /// </summary>
    public string? TestKpTag { get; set; }

    public double? Kp { get; set; }

    public double? Ti { get; set; }

    public double? Td { get; set; }
}

/// <summary>
/// Запрос проверки одного тега PID Tune.
/// PV/SP требуют trend-reference.
/// Test Kp проверяется только как online numeric tag.
/// </summary>
public sealed class ParamTuneCheckRequest
{
    public string TagName { get; set; } = "";

    /// <summary>
    /// true для PV/SP, false для Test Kp.
    /// </summary>
    public bool RequireTrend { get; set; } = true;
}

/// <summary>
/// Результат проверки тега и текущего TagRead-значения.
/// </summary>
public sealed class ParamTuneCheckResponse
{
    public string TagName { get; set; } = "";

    /// <summary>
    /// Итог проверки.
    /// Для trend-тега: numeric TagRead + trend-reference.
    /// Для online-тега: только numeric TagRead.
    /// </summary>
    public bool Found { get; set; }

    /// <summary>
    /// Требовался ли trend-reference для этой проверки.
    /// </summary>
    public bool TrendRequired { get; set; } = true;

    public bool TrendFound { get; set; }

    public double? CurrentValue { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// Runtime-данные вкладки Tune: сохраненные настройки, текущие значения и line-тренды.
/// </summary>
public sealed class ParamTuneRuntimeResponse
{
    public string EquipmentName { get; set; } = "";

    public EquipmentTypeGroup TypeGroup { get; set; } = EquipmentTypeGroup.Unknown;

    public bool Supported { get; set; }

    public string? Message { get; set; }

    public ParamTuneSettingsResponse Settings { get; set; } = new();

    public double? ManTuneValue { get; set; }

    public double? ManTuneMin { get; set; }

    public double? ManTuneMax { get; set; }

    public double? PvValue { get; set; }

    public double? SpValue { get; set; }

    /// <summary>
    /// Текущее online-значение Test Kp.
    /// В тренд оно намеренно не добавляется.
    /// </summary>
    public double? TestKpValue { get; set; }

    public ParamTrendResponse Trend { get; set; } = new();

    public DateTime Time { get; set; } = DateTime.Now;
}
