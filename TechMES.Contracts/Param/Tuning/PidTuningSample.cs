namespace TechMES.Contracts.Param.Tuning;

/// <summary>
/// Одна точка тренда для PID Tune.
/// Время хранится в UTC, чтобы расчет не зависел от локального часового пояса WEB-клиента.
/// </summary>
public sealed record PidTuningSample(DateTime TimeUtc, double Value);
