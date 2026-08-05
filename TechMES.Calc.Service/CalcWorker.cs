using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TechMES.Calc.Service;

/// <summary>
/// Фоновая служба расчётов TechMES.
///
/// На первом этапе служба только запускается и корректно ожидает остановки.
/// Подключение к Runtime, загрузка заданий и выполнение расчётов будут
/// добавлены после создания расчётного ядра и контрактов.
/// </summary>
public sealed class CalcWorker(ILogger<CalcWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TechMES Calc Service started.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальное завершение Windows-службы.
        }

        logger.LogInformation("TechMES Calc Service stopped.");
    }
}