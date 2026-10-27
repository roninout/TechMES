using System.Text.Json;
using TechMES.Application.Users;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>Использует тот же ключ пользователя, который WEB передаёт для Favorites.</summary>
public static class UserStateEndpoints
{
    public static IEndpointRouteBuilder MapUserStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/user/state", GetAsync);
        app.MapPatch("/api/user/state", PatchAsync);

        return app;
    }

    private static async Task<IResult> GetAsync(string? userName, IUserStateStore store, CancellationToken ct)
    {
        if (!ValidOwner(userName))
            return Results.Problem("Windows login is required.", statusCode: 403);

        return Results.Ok(await store.GetAsync(userName!, ct));
    }

    private static async Task<IResult> PatchAsync(string? userName, JsonElement changes, IUserStateStore store, CancellationToken ct)
    {
        if (!ValidOwner(userName))
            return Results.Problem("Windows login is required.", statusCode: 403);

        try
        {
            await store.PatchAsync(userName!, changes, ct);
            return Results.NoContent();
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { Error = ex.Message });
        }
    }

    private static bool ValidOwner(string? userName) => !string.IsNullOrWhiteSpace(userName) && userName.Trim().Length <= 256 && !userName.Trim().Equals("__anonymous__", StringComparison.OrdinalIgnoreCase);

}