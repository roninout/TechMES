namespace TechMES.Application.Scada;

/// <summary>
/// Разрешение записей из фоновых операций adapter-а, включая heartbeat CtApi.
/// Runtime предоставляет одну проверку для HTTP, Calc и внутренних операций.
/// </summary>
public interface IPlantScadaWritePermission
{
    bool CanWrite { get; }

    string? WriteEpoch { get; }

    bool CanWriteInEpoch(string? epoch);
}