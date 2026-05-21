using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public interface IDbService
    {
        Task<bool> CanConnectAsync(CancellationToken ct = default);

        Task<IReadOnlyList<OperatorActDTO>> GetOperatorActsAsync(DateTime date, string? equipFilter, CancellationToken ct = default);

        Task<IReadOnlyList<AlarmHistoryDTO>> GetAlarmHistoryAsync(DateTime date, string? equipFilter, CancellationToken ct = default);
    }
}
