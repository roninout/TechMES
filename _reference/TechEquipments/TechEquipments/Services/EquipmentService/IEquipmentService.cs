using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public interface IEquipmentService
    {
        public sealed record LoadingProgress(int TotalTrends, int CurrentTrendIndex, string CurrentTrendName, int CurrentTrendCount, int TotalLoaded);

        Task<string> GetTrnName(string sEquipName, string sEquipItem);

        Task<List<EquipmentSOEDto>> GetTrnByEquipment(EquipRefModel equipment, IProgress<int>? progress = null, CancellationToken ct = default, int maxRows = 2000);

        Task<List<string>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem ="STW");
        Task<List<PlcRefRow>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem, string sCustField = "CUSTOM1");

        /// <summary>
        /// Ищет связанную equipment-ссылку в категории WinOpened.
        /// 
        /// Возвращает WinOpenedRefResult c полями:
        /// - RefEquip (REFEQUIP)
        /// - Assoc    (ASSOC)
        /// - RefItem  (REFITEM)
        /// 
        /// Если assocExpected == null:
        /// - возвращает первую валидную REFEQUIP
        /// 
        /// Если assocExpected задан:
        /// - возвращает первую валидную REFEQUIP только для нужного ASSOC
        ///   (например "_dryRunAI" или "_dryRunDI")
        /// </summary>
        Task<WinOpenedRefResult?> GetWinOpenedRefAsync(string sEquipName,string sEquipItem,string sCategory = "WinOpened",string? assocExpected = null);

        Task<EquipRefModel> GetEquipData(string sEquipName, string sEquipItem = "STW");
        Task<EquipModel> GetEquipModelWithRef(string sEquipName, string sEquipItem = "STW");

        Task<List<EquipmentSOEDto>> GetDataFromEquipAsync(string equipName, IProgress<LoadingProgress>? progress = null, CancellationToken ct = default, int perTrendMax = 2000, int totalMax = 10000);
        Task<List<EquipListBoxItem>> GetAllEquipmentsAsync(IProgress<(int done, int total)>? progress = null, CancellationToken ct = default);

        Task<string> GetExternalTagAsync(CancellationToken ct = default);
        Task SetExternalTagAsync(string value, CancellationToken ct = default);

        Task<T> ReadEquipParamsAsync<T>(string equipName, CancellationToken ct = default) where T : new();

        Task WriteEquipItemAsync<T>(string equipName, string equipItem, T value, CancellationToken ct = default);


        Task<string> ResolveTagNameAsync(string equipName, string equipItem, CancellationToken ct = default);
        Task WriteTagNameAsync(string tagName, string value, CancellationToken ct = default);

    }
}
