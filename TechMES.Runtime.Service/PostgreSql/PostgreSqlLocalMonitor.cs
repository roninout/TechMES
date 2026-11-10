using TechMES.Contracts.Runtime;
using TechMES.PostgreSql.Control;

namespace TechMES.Runtime.Service.PostgreSql;

/// <summary>Сохраняет существующий интерфейс Runtime поверх общего локального модуля.</summary>
public sealed class PostgreSqlLocalMonitor(PostgreSqlNodeMonitor node)
{
    /// <summary>Возвращает готовый снимок локального PostgreSQL.</summary>
    public PostgreSqlLocalHealthResponse GetHealth()
    {
        return node.GetHealth();
    }

    /// <summary>Обновляет состояние независимо от режима Runtime Active/Standby.</summary>
    public Task RefreshAsync(CancellationToken ct)
    {
        return node.RefreshAsync(ct);
    }
}