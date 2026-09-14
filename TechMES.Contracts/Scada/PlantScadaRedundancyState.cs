namespace TechMES.Contracts.Scada;

public sealed record PlantScadaServerState
{
    public string Role { get; init; } = "";
    public string Server { get; init; } = "";
    public bool Configured { get; init; }
    public bool Connected { get; init; }
    public string Message { get; init; } = "Not checked.";

    public string WriteTag { get; init; } = "";
    public string ReadTag { get; init; } = "";

    // Последнее прочитанное значение Write-тега.
    public string? WriteValue { get; init; }

    // Последняя подтверждённая запись heartbeat.
    public string? LastWrittenValue { get; init; }
    public bool? WriteTagReadOk { get; init; }
    public bool? WriteOk { get; init; }
    public string WriteMessage { get; init; } = "Not checked.";

    // Ответ PLC. Не зависит от Connected и результата heartbeat-записи.
    public string? ReadValue { get; init; }
    public bool? ReadOk { get; init; }
    public string ReadMessage { get; init; } = "Not checked.";
    public DateTimeOffset? ReadAtUtc { get; init; }
}

public sealed record PlantScadaRedundancyState
{
    public int HealthPeriodSeconds { get; init; } = 10;
    public string? ActiveRole { get; init; }
    public string? ActiveServer { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }

    public PlantScadaServerState Primary { get; init; } = new();
    public PlantScadaServerState Secondary { get; init; } = new();
}