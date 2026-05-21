namespace TechEquipments
{
    /// <summary>
    /// Состояние загрузки Param-данных/секции.
    /// Waiting   - ещё ждём
    /// Ready     - данные успешно загружены
    /// Unavailable - данных/связей нет, ждать больше нечего
    /// Error     - произошла ошибка, overlay тоже надо закрыть
    /// </summary>
    public enum ParamLoadState
    {
        Waiting = 0,
        Ready = 1,
        Unavailable = 2,
        Error = 3
    }
}