using TechMES.Contracts.Param;

namespace TechMES.Application.Param;

/// <summary>
/// Хранилище PID Tune-настроек, привязанных к конкретному оборудованию.
/// </summary>
public interface IParamTuneStore
{
    Task<ParamTuneSettingsResponse?> GetAsync(
        string equipmentName,
        CancellationToken ct = default);

    Task<ParamTuneSettingsResponse> SaveAsync(
        string equipmentName,
        ParamTuneSaveRequest request,
        CancellationToken ct = default);
}
