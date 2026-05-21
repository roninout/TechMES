using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public interface IMessageService
    {
        Task EnsureTableAsync(CancellationToken ct = default);

        Task<IReadOnlyList<EquipmentMessageDto>> GetMessagesAsync(bool includeInactive, string deviceName, CancellationToken ct = default);

        Task<int> GetActiveMessageCountAsync(CancellationToken ct = default);

        Task<EquipmentMessageDto> SaveMessageAsync(EquipmentMessageDto message, string userName, string deviceName, CancellationToken ct = default);

        Task<bool> ToggleActivityAsync(long messageId, string userName, CancellationToken ct = default);

        Task DeleteMessageAsync(long messageId, CancellationToken ct = default);

        Task MarkViewedAsync(long messageId, string deviceName, CancellationToken ct = default);
    }
}