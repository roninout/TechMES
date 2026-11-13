using TechMES.Contracts.Runtime;

namespace TechMES.Web.Clients;

/// <summary>
/// Результат прямого чтения одного Runtime.
/// Ошибка связи не подменяется режимом Standby.
/// </summary>
public sealed record RuntimeEndpointStatus(Uri Endpoint, RuntimeActivityStatusResponse? Activity, DateTimeOffset CheckedAtUtc, string Error)
{
    /// <summary>
    /// Условие готовности, общее для HTTP-маршрутизации и live-соединения.
    /// </summary>
    public bool IsActive => Activity is { CanExecuteCommands: true }
        && string.Equals(Activity.Mode, "Active", StringComparison.OrdinalIgnoreCase);
}