using TechMES.Contracts.Equipment;

namespace TechMES.Web.State;

/// <summary>
/// Состояние выбранного оборудования для текущего WEB-клиента.
///
/// В Blazor Server scoped-сервис живёт внутри одного пользовательского circuit.
/// Это значит:
/// - у каждого открытого браузера свой SelectedEquipmentState;
/// - выбор оборудования одного пользователя не меняет выбор другого пользователя;
/// - разные страницы одного пользователя могут видеть одно и то же выбранное оборудование.
/// </summary>
public sealed class SelectedEquipmentState
{
    /// <summary>
    /// Внутреннее значение выбранного оборудования.
    /// Наружу отдается только read-only property, чтобы изменение всегда проходило через SetSelectedEquipment.
    /// </summary>
    private EquipmentDto? _selectedEquipment;

    /// <summary>
    /// Событие изменения выбранного оборудования.
    /// Компоненты могут подписаться и обновить UI.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Текущее выбранное оборудование.
    /// </summary>
    public EquipmentDto? SelectedEquipment => _selectedEquipment;

    /// <summary>
    /// Имя выбранного оборудования.
    /// Удобно для будущих API-запросов: /api/param/{equipmentName}, /api/info/{equipmentName}.
    /// </summary>
    public string? SelectedEquipmentName => _selectedEquipment?.Name;

    /// <summary>
    /// Установить выбранное оборудование.
    /// </summary>
    public void SetSelectedEquipment(EquipmentDto? equipment)
    {
        _selectedEquipment = equipment;
        Changed?.Invoke();
    }

    /// <summary>
    /// Очистить выбор.
    /// </summary>
    public void Clear()
    {
        SetSelectedEquipment(null);
    }
}
