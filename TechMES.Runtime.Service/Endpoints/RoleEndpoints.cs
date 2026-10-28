using Microsoft.Extensions.Options;
using TechMES.Contracts.Security;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Определение ролей пользователя по общей конфигурации Runtime.
/// На этом этапе endpoint используется только для отображения.
/// </summary>
public static class RoleEndpoints
{
    /// <summary>
    /// Регистрирует определение ролей по Windows-группам.
    /// IOptionsMonitor позволяет читать обновлённые привязки без перезапуска Runtime.
    /// </summary>
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/security/roles/resolve", (RoleResolutionRequest request, IOptionsMonitor<RoleGroupOptions> options) =>
        {
            return Results.Ok(options.CurrentValue.Resolve(request.Groups ?? []));
        });

        return app;
    }
}