using Microsoft.Extensions.Configuration;
using Npgsql;
using TechMES.Application.Users;
using TechMES.Contracts.Users;

namespace TechMES.Infrastructure.PostgreSql.Users;

/// <summary>Использует основную БД Info/Favorites; отдельная строка подключения не нужна.</summary>
public sealed class PostgreSqlUserAppearanceStore : IUserAppearanceStore
{
    private readonly string _connectionString;

    public PostgreSqlUserAppearanceStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default") ?? configuration["Database:ConnectionString"] ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");
    }

    public async Task<UserAppearanceDto> GetAsync(string userName, CancellationToken ct = default)
    {
        var owner = NormalizeOwner(userName);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = """
        SELECT dark_theme FROM public.web_user_appearance WHERE user_name = @user_name;
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_name", owner);

        // Отсутствие сохранённой строки означает стандартную светлую тему.
        return new() { IsDarkTheme = await command.ExecuteScalarAsync(ct) is true };
    }

    public async Task SaveAsync(string userName, UserAppearanceDto appearance, CancellationToken ct = default)
    {
        var owner = NormalizeOwner(userName);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = """
        INSERT INTO public.web_user_appearance (user_name, dark_theme, updated_at)
        VALUES (@user_name, @dark_theme, now())
        ON CONFLICT (user_name) DO UPDATE
        SET dark_theme = EXCLUDED.dark_theme, updated_at = now();
        """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_name", owner);
        command.Parameters.AddWithValue("dark_theme", appearance.IsDarkTheme);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string NormalizeOwner(string userName)
    {
        var owner = (userName ?? "").Trim();

        if (owner.Length is 0 or > 256 || owner.Equals("__anonymous__", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A logged-in user is required to save appearance.");

        // Windows-имя не зависит от регистра ввода в окне входа.
        return owner.ToUpperInvariant();
    }
}