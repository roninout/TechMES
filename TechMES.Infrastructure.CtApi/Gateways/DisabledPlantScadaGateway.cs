using TechMES.Application.Scada;
using TechMES.Contracts.Scada;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Adapter для режима, когда Plant SCADA integration отключена.
/// 
/// Он нужен, чтобы Runtime.Service мог стартовать даже без CtApi.
/// </summary>
public sealed class DisabledPlantScadaGateway : IPlantScadaGateway
{
    /// <summary>
    /// В Disabled-режиме инициализация ничего не делает.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Возвращает health-ответ о том, что Plant SCADA отключена настройкой.
    /// </summary>
    public Task<PlantScadaHealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new PlantScadaHealthResponse
        {
            Provider = "Disabled",
            Status = PlantScadaConnectionStatus.Disabled,
            IsConnected = false,
            Message = "Plant SCADA adapter отключён настройкой CtApi:Provider = Disabled.",
            Time = DateTime.Now
        });
    }

    /// <summary>
    /// Запрещает чтение tag-а, потому что CtApi provider выключен.
    /// </summary>
    public Task<ScadaTagReadResponse> ReadTagAsync(string tagName, CancellationToken ct = default)
    {
        return Task.FromResult(new ScadaTagReadResponse
        {
            TagName = tagName,
            Success = false,
            Error = "Plant SCADA adapter is disabled.",
            Time = DateTime.Now
        });
    }

    /// <summary>
    /// Запрещает запись tag-а, потому что CtApi provider выключен.
    /// </summary>
    public Task<ScadaTagWriteResponse> WriteTagAsync(ScadaTagWriteRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ScadaTagWriteResponse
        {
            TagName = request.TagName,
            WrittenValue = request.Value,
            Success = false,
            Error = "Plant SCADA adapter is disabled.",
            Time = DateTime.Now
        });
    }
}
