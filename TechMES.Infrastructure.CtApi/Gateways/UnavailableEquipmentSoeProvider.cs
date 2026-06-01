using TechMES.Application.Soe;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Soe;

namespace TechMES.Infrastructure.CtApi.Gateways;

/// <summary>
/// Заглушка SOE-провайдера для режима, когда SOE через CtApi недоступен.
/// Нужна, чтобы endpoint возвращал контролируемый ответ, а не ошибку DI/старта сервиса.
/// </summary>
public sealed class UnavailableEquipmentSoeProvider : IEquipmentSoeProvider
{
    /// <summary>
    /// Текст причины недоступности, который будет показан в SOE-вкладке.
    /// </summary>
    private readonly string _message;

    /// <summary>
    /// Создает заглушку с общей диагностической причиной недоступности.
    /// </summary>
    public UnavailableEquipmentSoeProvider(string message)
    {
        _message = message;
    }

    /// <summary>
    /// Возвращает пустой SOE-ответ без обращения к Plant SCADA.
    /// </summary>
    public Task<SoeResponse> GetSoeAsync(
        EquipmentDto equipment,
        IReadOnlyList<EquipmentDto> equipmentCatalog,
        int perTrendMax = 1000,
        int totalMax = 100,
        CancellationToken ct = default)
    {
        return Task.FromResult(new SoeResponse
        {
            EquipmentName = equipment.Name,
            Supported = false,
            Message = _message,
            LoadedAt = DateTime.Now
        });
    }
}
