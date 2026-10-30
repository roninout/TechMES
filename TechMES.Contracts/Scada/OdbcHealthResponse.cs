namespace TechMES.Contracts.Scada;

/// <summary>
/// Готовая диагностика ODBC.
/// Строки подключения и пароли в WEB не передаются.
/// </summary>
public sealed record OdbcHealthResponse
{
    public bool Enabled { get; init; }
    public int HealthPeriodSeconds { get; init; } = 10;
    public string? ActiveRole { get; init; }
    public OdbcServerState Primary { get; init; } = new() { Role = "Primary" };
    public OdbcServerState Secondary { get; init; } = new() { Role = "Secondary" };
}

public sealed record OdbcServerState
{
    public string Role { get; init; } = "";
    public bool Configured { get; init; }
    public bool Connected { get; init; }
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public string Message { get; init; } = "Not checked.";
}