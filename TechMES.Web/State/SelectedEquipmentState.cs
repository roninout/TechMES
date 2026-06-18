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
    /// Route информационного режима Equipment.
    /// </summary>
    private const string EquipmentInfoRoute = "/equipment";

    /// <summary>
    /// Route режима параметров Equipment.
    /// </summary>
    private const string EquipmentParamRoute = "/equipment/param";

    /// <summary>
    /// Внутреннее значение выбранного оборудования.
    /// Наружу отдается только read-only property, чтобы изменение всегда проходило через SetSelectedEquipment.
    /// </summary>
    private EquipmentDto? _selectedEquipment;

    /// <summary>
    /// Последний выбранный режим Equipment. Header использует его, чтобы вернуться из журналов
    /// именно в Info или Param, где оператор работал до перехода.
    /// </summary>
    private string _equipmentRoute = EquipmentInfoRoute;

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
    /// Route последнего выбранного режима Equipment.
    /// </summary>
    public string EquipmentRoute => _equipmentRoute;

    /// <summary>
    /// Установить выбранное оборудование.
    /// </summary>
    public void SetSelectedEquipment(EquipmentDto? equipment)
    {
        _selectedEquipment = equipment;
        Changed?.Invoke();
    }

    /// <summary>
    /// Запомнить, какой режим Equipment был выбран последним: Info или Param.
    /// </summary>
    public void SetEquipmentMode(bool isParamMode)
    {
        var route = isParamMode ? EquipmentParamRoute : EquipmentInfoRoute;
        if (string.Equals(_equipmentRoute, route, StringComparison.OrdinalIgnoreCase))
            return;

        _equipmentRoute = route;
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
