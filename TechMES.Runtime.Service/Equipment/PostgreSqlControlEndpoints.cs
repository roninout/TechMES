using System.Net;
using TechMES.Runtime.Service.PostgreSql;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

public static class PostgreSqlControlEndpoints
{
    /// <summary>Добавляет локальную диагностику, доступную также в Standby.</summary>
    public static IEndpointRouteBuilder MapPostgreSqlControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/database/local/health", GetHealth).WithMetadata(new RuntimeControlEndpoint());

        return app;
    }

    /// <summary>Разрешает локальное TCP-подключение и отдаёт готовый снимок без запуска проверок.</summary>
    private static IResult GetHealth(HttpContext context, PostgreSqlLocalMonitor monitor)
    {
        var remote = context.Connection.RemoteIpAddress;
        var local = context.Connection.LocalIpAddress;

        var sameComputer = remote is not null &&
            (IPAddress.IsLoopback(remote) || local is not null && remote.MapToIPv6().Equals(local.MapToIPv6()));

        return sameComputer
            ? Results.Ok(monitor.GetHealth())
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }
}