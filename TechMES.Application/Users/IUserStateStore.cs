using System.Text.Json;
using TechMES.Contracts.Users;

namespace TechMES.Application.Users;

/// <summary>Общее JSON-хранилище состояния пользователя; файл на диске не используется.</summary>
public interface IUserStateStore
{
    Task<UserStateDto> GetAsync(string userName, CancellationToken ct = default);
    Task PatchAsync(string userName, JsonElement changes, CancellationToken ct = default);
}