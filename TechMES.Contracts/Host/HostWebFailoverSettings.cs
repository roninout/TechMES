namespace TechMES.Contracts.Host;

/// <summary>Автоматическая передача при отказе WEB; недоступность Host не разрешает захват IP.</summary>
public sealed class HostWebFailoverSettings
{
    public bool Enabled { get; set; }
    public int FailureThreshold { get; set; } = 4;
    public int RecoveryThreshold { get; set; } = 3;

    /// <summary>Пороги задают число последовательных наблюдений состояния двух доступных Host.</summary>
    public void Validate()
    {
        if (FailureThreshold is < 2 or > 60 || RecoveryThreshold is < 2 or > 60)
            throw new InvalidOperationException("Пороги автоматической передачи WEB должны быть от 2 до 60.");
    }
}

public sealed record HostWebFailoverStatus(bool Enabled, string State, int OwnerFailures, int TargetSuccesses, string Details);