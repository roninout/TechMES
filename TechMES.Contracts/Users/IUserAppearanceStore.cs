using TechMES.Contracts.Users;

namespace TechMES.Application.Users;

/// <summary>Хранит оформление под именем вошедшего пользователя, как Favorites.</summary>
public interface IUserAppearanceStore
{
    Task<UserAppearanceDto> GetAsync(string userName, CancellationToken ct = default);
    Task SaveAsync(string userName, UserAppearanceDto appearance, CancellationToken ct = default);
}