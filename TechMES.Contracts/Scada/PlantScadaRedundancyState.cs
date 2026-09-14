namespace TechMES.Contracts.Scada;

public sealed record PlantScadaServerState
{
    public string Role { get; init; } = "";
    public string Server { get; init; } = "";
    public bool Configured { get; init; }
    public bool Connected { get; init; }
    public string Message { get; init; } = "Not checked.";

    public string ControlTag { get; init; } = "";
    public string? TagValue { get; init; }
    public bool? TagReadOk { get; init; }
    public bool? TagWriteOk { get; init; }
    public string TagMessage { get; init; } = "Not checked.";
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