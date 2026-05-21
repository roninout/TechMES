namespace TechEquipments
{
    /// <summary>
    /// Общий runtime-контекст приложения.
    /// Содержит глобальные readonly/runtime данные, доступные через DI.
    /// </summary>
    public interface IAppRuntimeContext
    {
        string DeviceName { get; }
        bool IsTablet { get; }
        string AppVersion { get; }
        int DevicePrivilege { get; }
    }
}
