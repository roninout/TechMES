using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public interface IUserStateService
    {
        Task<UserState?> LoadAsync(CancellationToken ct = default);
        Task SaveAsync(UserState state, CancellationToken ct = default);
        string StateFilePath { get; }
    }

}
