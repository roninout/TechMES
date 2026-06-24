namespace TechMES.Maintenance.Models;

/// <summary>
/// Нормализованный результат операции с Windows Service.
/// UI не должен парсить сырой вывод sc.exe, поэтому сервисный слой возвращает готовые поля.
/// </summary>
public sealed class ServiceCommandResult
{
    /// <summary>
    /// Успешно ли выполнена операция.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Короткий статус, например Running, Stopped или Not installed.
    /// </summary>
    public string Status { get; init; } = "";

    /// <summary>
    /// Подробности команды для диагностики.
    /// </summary>
    public string Details { get; init; } = "";
}
