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

    public double? Kp { get; set; }

    public double? Ti { get; set; }

    public double? Td { get; set; }
}

/// <summary>
/// Запрос проверки трендового тега PV/SP.
/// </summary>
public sealed class ParamTuneCheckRequest
{
    public string TagName { get; set; } = "";
}

/// <summary>
/// Результат проверки трендового тега и текущего TagRead-значения.
/// </summary>
public sealed class ParamTuneCheckResponse
{
    public string TagName { get; set; } = "";

    public bool Found { get; set; }

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

    public ParamTrendResponse Trend { get; set; } = new();

    public DateTime Time { get; set; } = DateTime.Now;
}
