namespace TechMES.Maintenance;

public partial class MainWindow
{
    // Использует общую блокировку deployment и закрытия окна на время переключения БД.
    public async Task RunPostgreSqlSwitchAsync(Func<Task> operation)
    {
        if (_deploymentOperationRunning)
            throw new InvalidOperationException("Дождитесь завершения текущей операции Maintenance.");

        _deploymentOperationRunning = true;

        try
        {
            await operation();
        }
        finally
        {
            _deploymentOperationRunning = false;
        }
    }
}