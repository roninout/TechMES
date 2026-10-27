using TechMES.Application.Users;
using TechMES.Contracts.Users;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>Имя владельца WEB передаёт из FavoriteOwnerContext, аналогично Favorites.</summary>
public static class UserAppearanceEndpoints
{
    public static IEndpointRouteBuilder MapUserAppearanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/user/appearance", GetAsync);
        app.MapPut("/api/user/appearance", SaveAsync);

        return app;
    }

    private static async Task<IResult> GetAsync(string? userName, IUserAppearanceStore store, CancellationToken ct)
    {
        if (!ValidOwner(userName))
            return Results.Problem("Windows login is required to use personal appearance.", statusCode: StatusCodes.Status403Forbidden);

        return Results.Ok(await store.GetAsync(userName!, ct));
    }

    private static async Task<IResult> SaveAsync(string? userName, UserAppearanceDto appearance, IUserAppearanceStore store, CancellationToken ct)
    {
        if (!ValidOwner(userName))
            return Results.Problem("Windows login is required to save personal appearance.", statusCode: StatusCodes.Status403Forbidden);

        await store.SaveAsync(userName!, appearance, ct);

        return Results.NoContent();
    }

    private static bool ValidOwner(string? userName)
    {
        var owner = userName?.Trim();

        return !string.IsNullOrEmpty(owner) && owner.Length <= 256 && !owner.Equals("__anonymous__", StringComparison.OrdinalIgnoreCase);
    }
}