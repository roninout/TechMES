namespace TechMES.Maintenance.Models;

/// <summary>
/// Профиль целевой серверной машины.
/// Это не меняет сетевые настройки автоматически, а дает оператору одну карточку ожиданий:
/// где должен жить сервер и какие PostgreSQL-подключения должны проходить авторизацию.
/// </summary>
public sealed class TargetMachineOptions
{
    /// <summary>
    /// Человеческое имя сервера или площадки.
    /// </summary>
    public string DisplayName { get; set; } = "Local TechMES server";

    /// <summary>
    /// Ожидаемое DNS/Windows имя машины.
    /// </summary>
    public string HostName { get; set; } = "";

    /// <summary>
    /// Ожидаемый IP-адрес для планшетов и рабочих мест.
    /// </summary>
    public string IpAddress { get; set; } = "";

    /// <summary>
    /// Свободная заметка оператора обслуживания.
    /// </summary>
    public string Notes { get; set; } = "";
}
