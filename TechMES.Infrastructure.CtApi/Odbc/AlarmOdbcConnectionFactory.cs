using System.Data.Common;
using System.Data.Odbc;

namespace TechMES.Infrastructure.CtApi.Odbc;

/// <summary>
/// Создаёт отдельное подключение для проверки или чтения.
/// Открытые подключения не разделяются между параллельными запросами.
/// </summary>
public interface IAlarmOdbcConnectionFactory
{
    DbConnection Create(string connectionString, int connectionTimeoutSeconds);
}

public sealed class AlarmOdbcConnectionFactory : IAlarmOdbcConnectionFactory
{
    public DbConnection Create(string connectionString, int connectionTimeoutSeconds)
    {
        return new OdbcConnection(connectionString) { ConnectionTimeout = connectionTimeoutSeconds };
    }
}