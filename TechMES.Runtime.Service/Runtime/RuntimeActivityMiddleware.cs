using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.Runtime.Service.Runtime;

/// <summary>
/// Разрешает служебный endpoint до активации и в Standby.
/// Эта метка не заменяет аутентификацию будущих управляющих HA-команд.
/// </summary>
public sealed class RuntimeControlEndpoint
{
}

public sealed class RuntimeActivityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RuntimeActivityState activity, MainDatabaseWriteGuard writeGuard)
    {
        var endpoint = context.GetEndpoint();

        // Неизвестный маршрут сохраняет обычный HTTP 404.
        if (endpoint is null || endpoint.Metadata.GetMetadata<RuntimeControlEndpoint>() is not null)
        {
            await next(context);
            return;
        }

        var safety = writeGuard.GetStatus();
        if (safety.WritesBlocked)
        {
            await RejectDatabaseAsync(context, safety);
            return;
        }

        RuntimeActivityLease lease;

        try
        {
            lease = activity.Acquire();
        }
        catch (RuntimeNotActiveException)
        {
            await RejectAsync(context, activity);
            return;
        }

        using (lease)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.CancellationToken))
        {
            var originalToken = context.RequestAborted;
            context.RequestAborted = stop.Token;

            try
            {
                await next(context);
            }
            catch (MainDatabaseWriteBlockedException ex)
            {
                await RejectDatabaseAsync(context, ex.Safety);
            }
            catch (RuntimeNotActiveException)
            {
                await RejectAsync(context, activity);
            }
            catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested && !originalToken.IsCancellationRequested)
            {
                await RejectAsync(context, activity);
            }
            finally
            {
                context.RequestAborted = originalToken;
            }
        }
    }

    /// <summary>Возвращает явную причину блокировки кластера; диагностика остаётся доступной.</summary>
    private static async Task RejectDatabaseAsync(HttpContext context, MainDatabaseWriteSafety safety)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { code = "database.write-blocked", message = safety.Message, writeSafety = safety }, CancellationToken.None);
    }

    private static async Task RejectAsync(HttpContext context, RuntimeActivityState activity)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        await context.Response.WriteAsJsonAsync(new { code = "runtime.not-active", message = "Runtime is not accepting work.", runtime = activity.GetStatus() }, CancellationToken.None);
    }
}
