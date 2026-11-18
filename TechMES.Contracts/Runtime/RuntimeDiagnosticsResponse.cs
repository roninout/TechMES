namespace TechMES.Contracts.Runtime;

public sealed record RuntimeDiagnosticsResponse(CalcNodeDiagnostics Calc, PostgreSqlClusterHealthResponse PostgreSql);

public sealed record CalcNodeDiagnostics(string Status, string? WindowsServiceStatus, string? LeaseOwner, DateTimeOffset? LastHeartbeatAtUtc);
