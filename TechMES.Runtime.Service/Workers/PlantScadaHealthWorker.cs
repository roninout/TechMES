using Microsoft.Extensions.Options;
using TechMES.Application.Scada;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Runtime.Service.Workers;

/// <summary>
/// Фоновый health worker для Plant SCADA adapter-а.
///
/// Он периодически вызывает GetHealthAsync().
/// Позже, когда добавим HealthCheckTag, можно будет дополнительно делать ReadTagAsync().
/// </summary>
public sealed class PlantScadaHealthWorker : BackgroundService
{
    private readonly IPlantScadaGateway _plantScadaGateway;
    private readonly IOptions<CtApiOptions> _options;
    private readonly ILogger<PlantScadaHealthWorker> _logger;

    public PlantScadaHealthWorker(IPlantScadaGateway plantScadaGateway, IOptions<CtApiOptions> options, ILogger<PlantScadaHealthWorker> logger)
    {
        _plantScadaGateway = plantScadaGateway;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        var seconds = Math.Max(5, options.HealthCheckPeriodSeconds);
        var period = TimeSpan.FromSeconds(seconds);

        _logger.LogInformation("PlantScadaHealthWorker запущен. Provider={Provider}, Period={Seconds} sec.", options.Provider, seconds);

        using var timer = new PeriodicTimer(period);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var health = await _plantScadaGateway.GetHealthAsync(stoppingToken);

                if (!health.IsConnected)
                {
                    _logger.LogWarning("Plant SCADA status: {Status}. {Message}", health.Status, health.Message);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка PlantScadaHealthWorker.");
            }
        }
    }
}