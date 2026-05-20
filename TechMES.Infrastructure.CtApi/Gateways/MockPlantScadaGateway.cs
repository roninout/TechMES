using System.Collections.Concurrent;
using TechMES.Application.Scada;
using TechMES.Contracts.Scada;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Mock adapter Plant SCADA.
/// 
/// Он нужен для безопасной разработки WEB/Runtime API без реального CtApi.
/// Значения tag-ов хранятся в памяти процесса.
/// Позже этот adapter заменим на CtApiPlantScadaGateway.
/// </summary>
public sealed class MockPlantScadaGateway : IPlantScadaGateway
{
    private readonly ConcurrentDictionary<string, string?> _tags =
        new(StringComparer.OrdinalIgnoreCase);

    public Task InitializeAsync(CancellationToken ct = default)
    {
        // Несколько тестовых tag-ов, чтобы можно было проверить API.
        _tags.TryAdd("S01.H01.P01.Run", "0");
        _tags.TryAdd("S01.H01.P01.Mode", "Auto");
        _tags.TryAdd("S01.H01.P01.Speed", "1450");
        _tags.TryAdd("S01.H01.LT01.Value", "45.2");

        return Task.CompletedTask;
    }

    public Task<PlantScadaHealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new PlantScadaHealthResponse
        {
            Provider = "Mock",
            Status = PlantScadaConnectionStatus.Mock,
            IsConnected = true,
            Message = "Plant SCADA gateway работает в Mock-режиме.",
            Time = DateTime.Now
        });
    }

    public Task<ScadaTagReadResponse> ReadTagAsync(string tagName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return Task.FromResult(new ScadaTagReadResponse
            {
                TagName = tagName,
                Success = false,
                Error = "TagName is required.",
                Time = DateTime.Now
            });
        }

        var normalizedTagName = tagName.Trim();

        _tags.TryGetValue(normalizedTagName, out var value);

        return Task.FromResult(new ScadaTagReadResponse
        {
            TagName = normalizedTagName,
            Value = value ?? "",
            Success = true,
            Time = DateTime.Now
        });
    }

    public Task<ScadaTagWriteResponse> WriteTagAsync(ScadaTagWriteRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TagName))
        {
            return Task.FromResult(new ScadaTagWriteResponse
            {
                TagName = request.TagName,
                WrittenValue = request.Value,
                Success = false,
                Error = "TagName is required.",
                Time = DateTime.Now
            });
        }

        var normalizedTagName = request.TagName.Trim();

        _tags[normalizedTagName] = request.Value;

        return Task.FromResult(new ScadaTagWriteResponse
        {
            TagName = normalizedTagName,
            WrittenValue = request.Value,
            Success = true,
            Time = DateTime.Now
        });
    }
}