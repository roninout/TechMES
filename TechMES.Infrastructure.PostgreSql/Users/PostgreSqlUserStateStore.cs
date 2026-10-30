using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TechMES.Application.Users;
using TechMES.Contracts.Users;
using TechMES.Infrastructure.PostgreSql.Connections;

namespace TechMES.Infrastructure.PostgreSql.Users;

/// <summary>
/// Сохраняет настройки пользователя через общий маршрут Main DB.
/// </summary>
public sealed class PostgreSqlUserStateStore : IUserStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly MainDatabaseFailover _database;

    public PostgreSqlUserStateStore(MainDatabaseFailover database)
    {
        _database = database;
    }

    public async Task<UserStateDto> GetAsync(string userName, CancellationToken ct = default)
    {
        var owner = NormalizeOwner(userName);

        await using var connection = await _database.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT settings::text FROM public.web_user_appearance WHERE user_name = @owner;", connection);

        command.Parameters.AddWithValue("owner", owner);

        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<UserStateDto>(json, JsonOptions) ?? throw new JsonException("User settings are empty.") : new();
    }

    public async Task PatchAsync(string userName, JsonElement changes, CancellationToken ct = default)
    {
        var owner = NormalizeOwner(userName);

        if (changes.ValueKind != JsonValueKind.Object)
            throw new JsonException("User settings must be a JSON object.");

        // Проверяем типы известных полей до записи. Неизвестные поля сохраняются как JSON.
        var state = changes.Deserialize<UserStateDto>(JsonOptions) ?? throw new JsonException("User settings are empty.");

        if (state.LastEquipmentsByFilter is null || state.SelectedTypeFilter is < 0 or > 10 || state.SelectedTab is < 0 or > 5 || state.QrCameraIndex is < 0)
            throw new JsonException("Invalid user settings.");

        await using var connection = await _database.OpenAsync(ct);

        const string sql = """
        INSERT INTO public.web_user_appearance (user_name, settings, updated_at)
        VALUES (@owner, @changes, now())
        ON CONFLICT (user_name) DO UPDATE SET
            settings = web_user_appearance.settings || EXCLUDED.settings ||
                CASE WHEN EXCLUDED.settings ? 'LastEquipmentsByFilter' THEN
                    jsonb_build_object('LastEquipmentsByFilter',
                        COALESCE(web_user_appearance.settings -> 'LastEquipmentsByFilter', '{}'::jsonb) ||
                        (EXCLUDED.settings -> 'LastEquipmentsByFilter'))
                ELSE '{}'::jsonb END,
            updated_at = now();
        """;

        // Запись выполняется один раз на уже выбранном подключении.
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add("changes", NpgsqlDbType.Jsonb).Value = changes.GetRawText();

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string NormalizeOwner(string userName)
    {
        var owner = (userName ?? "").Trim();

        if (owner.Length is 0 or > 256 || owner.Equals("__anonymous__", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A logged-in user is required.");

        return owner.ToUpperInvariant();
    }
}