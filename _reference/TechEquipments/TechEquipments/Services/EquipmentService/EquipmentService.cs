using CtApi;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static TechEquipments.IEquipmentService;

namespace TechEquipments
{
    public sealed class EquipmentService : IEquipmentService
    {
        private readonly ICtApiService _ctApiService;
        private readonly IConfiguration _config;
        private const int windowMinutes = 30;

        // Кешируем “реальное” имя тега, которое возвращает TagInfo(...,0), чтобы не дергать Cicode каждый раз.
        private readonly ConcurrentDictionary<string, string> _tagNameCache = new(StringComparer.OrdinalIgnoreCase);
        // Кешируем Unit (TagInfo(tag,1)), чтобы не дергать Cicode каждый цикл.
        private readonly ConcurrentDictionary<string, string> _tagUnitCache = new(StringComparer.OrdinalIgnoreCase);
        // Кешируем TagCheckIfExists, чтобы не делать проверку каждый цикл.
        private readonly ConcurrentDictionary<string, bool> _tagExistsCache = new(StringComparer.OrdinalIgnoreCase);
        // Кеш канала (EquipGetProperty Custom1,3)
        private readonly ConcurrentDictionary<string, string> _equipChanelCache = new(StringComparer.OrdinalIgnoreCase);
        // 1 раз на (equip + modelType) делаем TagCheckIfExists (чтобы не спамить каждый polling)
        private readonly ConcurrentDictionary<string, bool> _equipModelExistsChecked = new(StringComparer.OrdinalIgnoreCase);

        public EquipmentService(ICtApiService ctApiService, IConfiguration config)
        {
            _ctApiService = ctApiService;
            _config = config;
        }

        // формируем EquipmentSOEDto с данными по Equipment для отображение в таблице
        public async Task<List<EquipmentSOEDto>> GetDataFromEquipAsync(string equipName, IProgress<LoadingProgress>? progress = null, CancellationToken ct = default, int perTrendMax = 2000, int totalMax = 10000)
        {
            var model = await GetEquipModelWithRef(equipName);
            if (model?.MainModel == null)
                return new List<EquipmentSOEDto>();

            ct.ThrowIfCancellationRequested();

            var equipList = new List<EquipRefModel> { model.MainModel };
            if (model.RefEquipments != null && model.RefEquipments.Count > 0)
                equipList.AddRange(model.RefEquipments);

            equipList = equipList
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.TrnName))
                .GroupBy(e => e.TrnName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            int totalTrends = equipList.Count;

            var allRows = new List<EquipmentSOEDto>(capacity: Math.Min(totalMax, 10000));

            for (int i = 0; i < totalTrends; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (allRows.Count >= totalMax) break;

                var equip = equipList[i];

                int localCount = 0;
                progress?.Report(new LoadingProgress(
                    TotalTrends: totalTrends,
                    CurrentTrendIndex: i + 1,
                    CurrentTrendName: equip.TrnName,
                    CurrentTrendCount: 0,
                    TotalLoaded: allRows.Count));

                var localProgress = new Progress<int>(c =>
                {
                    localCount = c;
                    progress?.Report(new LoadingProgress(
                        TotalTrends: totalTrends,
                        CurrentTrendIndex: i + 1,
                        CurrentTrendName: equip.TrnName,
                        CurrentTrendCount: c,
                        TotalLoaded: allRows.Count + c));
                });

                var rows = await GetTrnByEquipment(equip, localProgress, ct, maxRows: perTrendMax);

                if (rows != null && rows.Count > 0)
                {
                    int remaining = totalMax - allRows.Count;
                    if (rows.Count > remaining)
                        allRows.AddRange(rows.Take(remaining));
                    else
                        allRows.AddRange(rows);
                }

                progress?.Report(new LoadingProgress(
                    TotalTrends: totalTrends,
                    CurrentTrendIndex: i + 1,
                    CurrentTrendName: equip.TrnName,
                    CurrentTrendCount: localCount,
                    TotalLoaded: allRows.Count));
            }

            ct.ThrowIfCancellationRequested();

            return allRows.OrderByDescending(r => r.TimeUtc).ToList();
        }

        #region Equipment

        // возвращает данные эквипмента
        public async Task<EquipRefModel> GetEquipData(string sEquipName, string sEquipItem = "STW")
        {
            sEquipItem = await ResolveSoeEquipItemAsync(sEquipName, sEquipItem);

            var sTagName = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 0)");
            var sEquipType = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Type\", 3)");
            var sEquipDescription = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Comment\", 1)");
            var sTrnName = await GetTrnName(sEquipName, sEquipItem);

            return new EquipRefModel
            {
                Name = sEquipName,
                TagName = sTagName,
                Type = sEquipType,
                Description = sEquipDescription,
                TrnName = sTrnName
            };
        }

        // возвращает моделт эквипмента с сылками
        public async Task<EquipModel> GetEquipModelWithRef(string sEquipName, string sEquipItem = "STW")
        {
            if (string.IsNullOrWhiteSpace(sEquipName))
                throw new ArgumentException("Equipment name is empty.", nameof(sEquipName));

            // Для main equipment определяем правильный SOE item: Atv -> STW01, остальные -> STW.
            var mainSoeItem = await ResolveSoeEquipItemAsync(sEquipName, sEquipItem);

            // главный эквип
            var main = await GetEquipData(sEquipName, sEquipItem);
            var model = new EquipModel { MainModel = main };

            // refs открываем от корректного item главного equipment
            var equipRefNames = await GetEquipRef(sEquipName, "TabDIDO", mainSoeItem) ?? new List<string>();

            // убираем мусор/дубликаты
            equipRefNames = equipRefNames
                .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (equipRefNames.Count == 0)
                return model;

            // грузим refs параллельно
            var tasks = equipRefNames.Select(n => GetEquipData(n)).ToArray();
            var refs = await Task.WhenAll(tasks);

            model.RefEquipments.AddRange(refs.Where(r => r != null));

            return model;
        }

        // возвращает список названий EquipRef
        public async Task<List<string>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem)
        {
            var sField = "REFEQUIP";
            var listEquip = new List<string>();

            var sCluster = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 17)");
            var sConnect = "CLUSTER=" + sCluster + ";EQUIP=" + sEquipName + ";REFCAT=" + sCategory;

            var hSession = await _ctApiService.CicodeAsync($"EquipRefBrowseOpen(\"{sConnect}\",\"{sField}\")");

            if (Convert.ToInt32(hSession) != -1)
            {
                var nNumRecords = await _ctApiService.CicodeAsync($"EquipRefBrowseNumRecords({hSession})");

                if (Convert.ToInt32(nNumRecords) > 0)
                {
                    var nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseFirst({hSession})");

                    while (Convert.ToInt32(nReturn) == 0)
                    {
                        var sEquip = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sField}\")");

                        if (sEquip != null && sEquip != "Unknown")
                            listEquip.Add(sEquip);

                        nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseNext({hSession})");
                    }

                }

                await _ctApiService.CicodeAsync($"EquipRefBrowseClose({hSession})");
            }

            return listEquip;
        }

        // возвращает PLC refs (теги), с CUSTOM + COMMENT + REFITEM
        public async Task<List<PlcRefRow>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem, string sCustField = "CUSTOM1")
        {
            var sField = "REFEQUIP";
            var sFieldRefItem = "REFITEM";
            var sFieldComment = "COMMENT";
            var list = new List<PlcRefRow>();

            // cluster по любому tag внутри equipment (как у тебя)
            var sCluster = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 17)");
            var sConnect = "CLUSTER=" + sCluster + ";EQUIP=" + sEquipName + ";REFCAT=" + sCategory;

            var hSession = await _ctApiService.CicodeAsync($"EquipRefBrowseOpen(\"{sConnect}\",\"\")");

            if (Convert.ToInt32(hSession) == -1)
                return list;

            try
            {
                var nNumRecords = await _ctApiService.CicodeAsync($"EquipRefBrowseNumRecords({hSession})");
                if (Convert.ToInt32(nNumRecords) <= 0)
                    return list;

                var nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseFirst({hSession})");

                while (Convert.ToInt32(nReturn) == 0)
                {
                    var sEquip = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sField}\")");
                    var sCustom = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sCustField}\")");
                    var sRefItem = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sFieldRefItem}\")");
                    var sComment = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sFieldComment}\")");

                    if (!string.IsNullOrWhiteSpace(sEquip) && !string.Equals(sEquip, "Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        var type = PlcRefRow.ParseCustom(sCustom);
                        list.Add(new PlcRefRow(sEquip, sRefItem ?? "", type, sComment ?? ""));
                    }

                    nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseNext({hSession})");
                }

                return list;
            }
            finally
            {
                // гарантированное закрытие
                await _ctApiService.CicodeAsync($"EquipRefBrowseClose({hSession})");
            }
        }

        /// <summary>
        /// Ищет связанную equipment-ссылку по категории "WinOpened".
        /// Читает поля:
        /// - REFEQUIP
        /// - ASSOC
        /// - REFITEM
        ///
        /// Логика:
        /// - если assocExpected == null:
        ///   возвращаем первую валидную REFEQUIP
        /// - если assocExpected != null:
        ///   возвращаем первую валидную REFEQUIP только для нужного ASSOC
        /// </summary>
        public async Task<WinOpenedRefResult?> GetWinOpenedRefAsync(string sEquipName, string sEquipItem, string sCategory = "WinOpened", string? assocExpected = null)
        {
            const string sFieldEquip = "REFEQUIP";
            const string sFieldAssoc = "ASSOC";
            const string sFieldRefItem = "REFITEM";

            // cluster по любому tag внутри equipment
            var sCluster = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 17)");
            var sConnect = "CLUSTER=" + sCluster + ";EQUIP=" + sEquipName + ";REFCAT=" + sCategory;

            var hSession = await _ctApiService.CicodeAsync($"EquipRefBrowseOpen(\"{sConnect}\",\"\")");
            if (Convert.ToInt32(hSession) == -1)
                return null;

            try
            {
                var nNumRecords = await _ctApiService.CicodeAsync($"EquipRefBrowseNumRecords({hSession})");
                if (Convert.ToInt32(nNumRecords) <= 0)
                    return null;

                var nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseFirst({hSession})");

                while (Convert.ToInt32(nReturn) == 0)
                {
                    var sEquip = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sFieldEquip}\")");
                    var sAssoc = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sFieldAssoc}\")");
                    var sRefItem = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sFieldRefItem}\")");

                    var refEquip = (sEquip ?? "").Trim();
                    var assoc = (sAssoc ?? "").Trim();
                    var refItem = (sRefItem ?? "").Trim();

                    // Если ищем конкретный ASSOC - пропускаем все остальные
                    if (!string.IsNullOrWhiteSpace(assocExpected) &&
                        !string.Equals(assoc, assocExpected, StringComparison.OrdinalIgnoreCase))
                    {
                        nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseNext({hSession})");
                        continue;
                    }

                    // Если нашли валидный REFEQUIP -> это то, что нужно
                    if (!string.IsNullOrWhiteSpace(refEquip) &&
                        !string.Equals(refEquip, "Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        return new WinOpenedRefResult(refEquip, assoc, refItem);
                    }

                    nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseNext({hSession})");
                }

                return null;
            }
            finally
            {
                // гарантированное закрытие
                await _ctApiService.CicodeAsync($"EquipRefBrowseClose({hSession})");
            }
        }

        // проверка на существования тега (c кешем)
        private async Task<bool> IsTagExistAsync(string tagName)
        {
            tagName = (tagName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return false;

            if (_tagExistsCache.TryGetValue(tagName, out var cached))
                return cached;

            var result = await _ctApiService.CicodeAsync($"TagCheckIfExists({tagName})");

            var ok = int.TryParse(result, out var exists) && exists == 1;
            _tagExistsCache[tagName] = ok;

            return ok;
        }

        // Возвращает список названий всех Equipment
        public async Task<List<EquipListBoxItem>> GetAllEquipmentsAsync(IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var findHashTags = await _ctApiService.FindAsync(
                "Tag",
                "Tag=*_HASHCODE",
                "",
                "EQUIPMENT",
                "TAG",
                "COMMENT");

            var findEquipTags = await _ctApiService.FindAsync(
                "Tag",
                "Tag=*_EQUIP",
                "",
                "EQUIPMENT",
                "TAG",
                "COMMENT");

            ct.ThrowIfCancellationRequested();

            static string GetStationName(string equipmentName)
            {
                int dot = equipmentName.IndexOf('.');
                return dot > 0 ? equipmentName.Substring(0, dot) : "";
            }

            var sourceEquipments = findHashTags
                .Where(d =>
                    d.TryGetValue("EQUIPMENT", out var eq) &&
                    !string.IsNullOrWhiteSpace(eq) &&
                    d.TryGetValue("TAG", out var tag) &&
                    !string.IsNullOrWhiteSpace(tag))
                .Select(d => new EquipListBoxItem
                {
                    Equipment = (d.TryGetValue("EQUIPMENT", out var eq) ? eq : "").Trim(),
                    Tag = (d.TryGetValue("TAG", out var tag) ? tag : "").Trim(),
                    Description = (d.TryGetValue("COMMENT", out var comment) ? comment : "").Trim()
                })
                .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Description))
                    .First())
                .OrderBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sourceGroups = findEquipTags
                .Where(d =>
                    d.TryGetValue("EQUIPMENT", out var eq) &&
                    !string.IsNullOrWhiteSpace(eq) &&
                    d.TryGetValue("TAG", out var tag) &&
                    !string.IsNullOrWhiteSpace(tag))
                .Select(d => new EquipListBoxItem
                {
                    Equipment = (d.TryGetValue("EQUIPMENT", out var eq) ? eq : "").Trim(),
                    Tag = (d.TryGetValue("TAG", out var tag) ? tag : "").Trim(),
                    Description = (d.TryGetValue("COMMENT", out var comment) ? comment : "").Trim(),
                    Type = "Equipment",
                    TypeGroup = EquipTypeGroup.Equipment,
                    Station = GetStationName((d.TryGetValue("EQUIPMENT", out var eq2) ? eq2 : "").Trim()),
                    IsGroup = true,
                    IsEquipmentChildNode = false
                })
                .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Description))
                    .First())
                .OrderBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int total = sourceEquipments.Count + sourceGroups.Count;
            int done = 0;
            progress?.Report((done, total));

            // 1) Обычное оборудование (плоские root nodes, как раньше)
            var plainEquipments = new List<EquipListBoxItem>(sourceEquipments.Count);
            int nextPlainId = 1;

            foreach (var item in sourceEquipments)
            {
                ct.ThrowIfCancellationRequested();

                item.Type = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{item.Equipment}\",\"Type\", 3)");

                if (!EquipTypeRegistry.IsSupportedType(item.Type))
                {
                    done++;
                    progress?.Report((done, total));
                    continue;
                }

                item.TypeGroup = EquipTypeRegistry.GetGroup(item.Type);
                item.Station = GetStationName(item.Equipment);

                item.NodeId = $"EQ:{nextPlainId++}";
                item.ParentNodeId = "0";
                item.IsGroup = false;
                item.IsEquipmentChildNode = false;

                plainEquipments.Add(item);

                done++;
                progress?.Report((done, total));
            }

            // lookup по обычному оборудованию
            var plainLookup = plainEquipments
                .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // 2) Финальный список: обычное оборудование + группы + child nodes
            var result = new List<EquipListBoxItem>(plainEquipments.Count + sourceGroups.Count * 4);
            result.AddRange(plainEquipments);

            int nextGroupId = 1;

            foreach (var group in sourceGroups)
            {
                ct.ThrowIfCancellationRequested();

                group.NodeId = $"GRP:{nextGroupId++}";
                group.ParentNodeId = "0";
                group.IsGroup = true;
                group.IsEquipmentChildNode = false;
                group.Type = "Equipment";
                group.TypeGroup = EquipTypeGroup.Equipment;
                group.Station = GetStationName(group.Equipment);

                result.Add(group);

                List<string> refs;
                try
                {
                    refs = await GetEquipRef(group.Equipment, "EquipGroup", "Value") ?? new List<string>();
                }
                catch
                {
                    refs = new List<string>();
                }

                int childIndex = 1;

                foreach (var equipName in refs
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!plainLookup.TryGetValue(equipName, out var src))
                        continue;

                    result.Add(new EquipListBoxItem
                    {
                        Equipment = src.Equipment,
                        Tag = src.Tag,
                        Type = src.Type,
                        Station = src.Station,
                        TypeGroup = src.TypeGroup,
                        Description = src.Description,

                        NodeId = $"CH:{group.NodeId}:{childIndex++}",
                        ParentNodeId = group.NodeId,
                        IsGroup = false,
                        IsEquipmentChildNode = true
                    });
                }

                done++;
                progress?.Report((done, total));
            }

            return result;
        }

        // читаем внешний тег для поиска
        public async Task<string> GetExternalTagAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var tagName = (_config["CtApi:ExternalTag"] ?? "").Trim();
            var v = await _ctApiService.TagReadAsync(tagName);

            ct.ThrowIfCancellationRequested();

            return (v ?? "").Trim();
        }

        /// <summary>
        /// Пишет строковое значение в внешний тег (CtApi:ExternalTag).
        /// Важно: для строк в Cicode/TagWrite обычно нужны кавычки.
        /// </summary>
        public async Task SetExternalTagAsync(string value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var tagName = (_config["CtApi:ExternalTag"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(tagName))
                throw new InvalidOperationException("CtApi:ExternalTag is empty in config.");

            value = (value ?? "").Trim();

            // Экранируем кавычки как для Cicode-строки: " -> ""
            var escaped = value.Replace("\"", "\"\"");

            // Передаём как строковый литерал Cicode: "..."
            var cicodeString = $"\"{escaped}\"";

            await _ctApiService.TagWriteAsync(tagName, cicodeString);

            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Возвращает правильный SOE item для equipment.
        /// Сейчас нужен один special-case:
        /// - Atv / AtvSiemens -> STW01
        /// Все остальные -> STW
        /// </summary>
        private async Task<string> ResolveSoeEquipItemAsync(string sEquipName, string sEquipItem = "STW")
        {
            sEquipItem = (sEquipItem ?? "").Trim();
            if (string.IsNullOrWhiteSpace(sEquipItem))
                sEquipItem = "STW";

            // Если уже явно передали не-STW, не переопределяем.
            if (!string.Equals(sEquipItem, "STW", StringComparison.OrdinalIgnoreCase))
                return sEquipItem;

            var sEquipType = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Type\", 3)");
            var group = EquipTypeRegistry.GetGroup(sEquipType);

            if (group == EquipTypeGroup.Atv)
            {
                // Для Atv SOE идёт по STW01.
                // На всякий случай проверим, что tag реально существует в конфигурации.
                var stw01TagName = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.STW01\", 0)");
                if (!string.IsNullOrWhiteSpace(stw01TagName) &&
                    !string.Equals(stw01TagName, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return "STW01";
                }
            }

            return "STW";
        }

        #endregion

        #region Trend
        public async Task<string> GetTrnName(string sEquipName, string sEquipItem)
        {
            var sCluster = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 17)");
            var sTrnName = await _ctApiService.CicodeAsync($"_SATrend_GetTrendTag(\"{sCluster}\", \"{sEquipName}\", \"{sEquipItem}\")");

            return sTrnName;
        }

        public async Task<List<EquipmentSOEDto>> GetTrnByEquipment(EquipRefModel equipment, IProgress<int> progress = null, CancellationToken ct = default, int maxRows = 2000)
        {
            if (equipment == null) throw new ArgumentNullException(nameof(equipment));
            if (string.IsNullOrWhiteSpace(equipment.TrnName)) return new List<EquipmentSOEDto>();

            var dayStartUtc = DateTime.SpecifyKind(DateTime.Now.Date, DateTimeKind.Local).ToUniversalTime();
            var endUtc = DateTime.UtcNow;

            var result = new List<EquipmentSOEDto>(capacity: maxRows);

            long? lastWord = null; // исходное слово для сравнения
            bool hasLast = false;

            while (result.Count < maxRows && endUtc > dayStartUtc)
            {
                ct.ThrowIfCancellationRequested();

                var startUtc = endUtc.AddMinutes(-windowMinutes);
                if (startUtc < dayStartUtc)
                    startUtc = dayStartUtc;

                var trnData = await _ctApiService.GetTrnData(equipment.TrnName, startUtc, endUtc);

                if (trnData != null)
                {
                    foreach (var x in trnData)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (x.ValueQuality.ToString() == "Bad")
                            break;

                        long curWord = ToLongWord(x.Value);

                        if (!hasLast)
                        {
                            // первую точку НЕ добавляем, только запоминаем базовое состояние
                            lastWord = curWord;
                            hasLast = true;
                            continue;
                        }

                        // "сжатие" по исходному слову
                        if (curWord == lastWord.Value)
                            continue;

                        ushort last16 = (ushort)(lastWord.Value & 0xFFFF);
                        ushort cur16 = (ushort)(curWord & 0xFFFF);

                        long bitCode = GetChangedBitCode(last16, cur16); // 1..32 или -99

                        result.Add(MapToDto(equipment, x, bitCode));

                        lastWord = curWord;

                        progress?.Report(result.Count);
                        if (result.Count >= maxRows)
                            break;
                    }
                }

                endUtc = startUtc.AddMilliseconds(-1);
            }

            //return result.OrderByDescending(x => x.TimeUtc).ToList();
            return result;
        }

        private static EquipmentSOEDto MapToDto(EquipRefModel equipment, TrnData x, long bitCode)
        {
            return new EquipmentSOEDto
            {
                TimeUtc = x.DateTime,
                TypeGroup = EquipTypeRegistry.GetGroup(equipment.Type ?? ""),
                //Type = equipment.Type ?? "",
                Equipment = equipment.Name ?? "",
                Event = SoeEventMapper.GetEventText(equipment.Type, (int)bitCode),
                EventKey = SoeEventMapper.GetEventKey(equipment.Type, (int)bitCode),
                BitCode = bitCode,
                TrnValue = x.Value,

                ValueQuality = x.ValueQuality.ToString(),
            };
        }
        #endregion

        #region BitLogic
        public static int GetChangedBitCode(ushort last, ushort cur)
        {
            ushort diff = (ushort)(cur ^ last);

            if (diff == 0)
                return -99;

            int bitPos = 0; // 1..16

            for (int i = 0; i < 16; i++)
            {
                if ((diff & (1 << i)) != 0)
                {
                    bitPos = i + 1; // первый изменившийся бит
                    break;
                }
            }

            // направление (появился/пропал)
            bool nowSet = (cur & (1 << (bitPos - 1))) != 0;
            return nowSet ? bitPos : (bitPos + 16); // 1..16 = появился, 17..32 = пропал
        }

        private static long ToLongWord(double value)
        {
            // если тренд возвращает целое в double (2313.0), то округление норм
            return Convert.ToInt64(Math.Round(value, MidpointRounding.AwayFromZero));
        }
        #endregion

        #region Read Params

        /// <summary>
        /// Читаем данные модели Param
        /// </summary>
        /// <summary>
        /// Читаем данные модели Param.
        /// Главная оптимизация: TagInfo("Equip.Item",0) берём из кеша (GetTagNameAsync),
        /// а не дергаем Cicode на каждое свойство каждый цикл.
        /// </summary>
        public async Task<T> ReadEquipParamsAsync<T>(string equipName, CancellationToken ct = default) where T : new()
        {
            ct.ThrowIfCancellationRequested();

            var name = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return default;

            var props = ReflectionCache.GetParamProperties(typeof(T));
            if (props.Length == 0)
                return new T();

            // 1) Быстрая проверка: первый тег должен существовать
            var firstItem = props[0].Name;

            var firstTagName = await GetTagNameAsync(name, firstItem, ct);
            firstTagName = (firstTagName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(firstTagName) || firstTagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return default;

            // Exists check: только 1 раз на (equip + modelType)
            var existKey = $"{name}|{typeof(T).FullName}";
            if (!_equipModelExistsChecked.TryGetValue(existKey, out var ok))
            {
                ok = await IsTagExistAsync(firstTagName);      // внутри уже есть _tagExistsCache
                _equipModelExistsChecked[existKey] = ok;
            }

            if (!ok)
                return default;

            // 2) Собираем пары (Property -> TagName)
            var model = new T();
            var pairs = new List<(PropertyInfo prop, string tagName)>(props.Length);

            foreach (var p in props)
            {
                ct.ThrowIfCancellationRequested();

                var equipItem = p.Name;

                var tagName = await GetTagNameAsync(name, equipItem, ct);
                tagName = (tagName ?? "").Trim();

                if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Unit читаем (и кешируем) только когда это реально нужно
                if (model is IHasUnit mUnit && equipItem == "R")
                    mUnit.Unit = await GetTagUnitAsync(tagName, ct);

                pairs.Add((p, tagName));
            }

            if (pairs.Count == 0)
            {
                if (model is IHasChanel mChEmpty)
                    mChEmpty.Chanel = await GetEquipChanelAsync(name, ct);

                return model;
            }

            // 3) Параллельное TagRead с лимитом из конфига
            var maxPar = _config.GetValue<int>("CtApi:TagReadParallelism", 1);
            if (maxPar < 1) maxPar = 1;

            var uniqueTags = pairs
                .Select(x => x.tagName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rawMap = await TagReadManyAsync(uniqueTags, maxPar, ct);

            // 4) Проставляем значения
            foreach (var (prop, tagName) in pairs)
            {
                ct.ThrowIfCancellationRequested();

                if (!rawMap.TryGetValue(tagName, out var raw) || raw == null)
                    continue;

                if (TryConvert(raw, prop.PropertyType, out var converted))
                    prop.SetValue(model, converted);
            }

            // 5) Chanel (кешируем навсегда)
            if (model is IHasChanel mCh)
                mCh.Chanel = await GetEquipChanelAsync(name, ct);

            return model;
        }

        /// <summary>
        /// Конвертация значения тега в нужный тип свойства модели
        /// </summary>
        private static bool TryConvert(string raw, Type targetType, out object? value)
        {
            value = null;
            raw = (raw ?? "").Trim();

            // Nullable<T>
            var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                if (t == typeof(string))
                {
                    value = raw;
                    return true;
                }

                if (t == typeof(bool))
                {
                    // CtApi часто отдаёт "0"/"1"
                    if (raw == "1") { value = true; return true; }
                    if (raw == "0") { value = false; return true; }
                    if (bool.TryParse(raw, out var b)) { value = b; return true; }
                    return false;
                }

                if (t == typeof(int))
                {
                    if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var i)) { value = i; return true; }
                    if (int.TryParse(raw, out i)) { value = i; return true; }
                    return false;
                }

                if (t == typeof(uint))
                {
                    if (uint.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var u)) { value = u; return true; }
                    if (uint.TryParse(raw, out u)) { value = u; return true; }
                    return false;
                }

                if (t == typeof(double))
                {
                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
                    if (double.TryParse(raw, out d)) { value = d; return true; }
                    return false;
                }

                // fallback для простых типов
                value = Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Write Params

        /// <summary>
        /// Универсальная запись значения в параметр оборудования:
        /// equipName.EquipItem -> TagInfo(...) -> TagWriteAsync(...)
        /// </summary>
        public async Task WriteEquipItemAsync<T>(string equipName, string equipItem, T value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(equipName))
                throw new ArgumentException("equipName is empty", nameof(equipName));

            if (string.IsNullOrWhiteSpace(equipItem))
                throw new ArgumentException("equipItem is empty", nameof(equipItem));

            // 1) Получаем имя тега (через TagInfo), с кешем
            var tagName = await GetTagNameAsync(equipName.Trim(), equipItem.Trim(), ct);
            if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"TagInfo returned empty/Unknown for {equipName}.{equipItem}");

            // 2) Конвертация в строку для записи (важно для double -> точка)
            var strValue = ToTagWriteString(value);

            // 3) Пишем
            await _ctApiService.TagWriteAsync(tagName, strValue);

            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Приведение значения к строке для TagWriteAsync.
        /// Важно: double -> InvariantCulture (точка).
        /// bool обычно пишут как 1/0 (если у тебя True/False — поменяй).
        /// </summary>
        private static string ToTagWriteString<T>(T value)
        {
            if (value is null) return "";

            return value switch
            {
                bool b => b ? "1" : "0",
                double d => d.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? ""
            };
        }

        #endregion

        #region Helpers
        /// <summary>
        /// Возвращает имя тега через TagInfo("Equip.Item",0). Использует кеш.
        /// </summary>
        private async Task<string> GetTagNameAsync(string equipName, string equipItem, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var cacheKey = $"{equipName}.{equipItem}";
            if (_tagNameCache.TryGetValue(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
                return cached;

            var tagName = await _ctApiService.CicodeAsync($"TagInfo(\"{equipName}.{equipItem}\", 0)");
            tagName = (tagName ?? "").Trim();

            // Кешируем даже Unknown, чтобы не спамить Cicode (можно не кешировать Unknown — на твой вкус)
            _tagNameCache[cacheKey] = tagName;

            return tagName;
        }

        // <summary>
        /// Возвращает Unit тега через TagInfo("TagName",1). Использует кеш.
        /// </summary>
        private async Task<string> GetTagUnitAsync(string tagName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            tagName = (tagName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return "";

            if (_tagUnitCache.TryGetValue(tagName, out var cached))
                return cached ?? "";

            string unit;
            try
            {
                unit = await _ctApiService.CicodeAsync($"TagInfo(\"{tagName}\", 1)");
                unit = (unit ?? "").Trim();
            }
            catch
            {
                unit = "";
            }

            _tagUnitCache[tagName] = unit;
            return unit;
        }

        /// <summary>
        /// Возвращает Chanel для оборудования через EquipGetProperty("equip","Custom1",3).
        /// Кешируем навсегда (считаем константой).
        /// </summary>
        private async Task<string> GetEquipChanelAsync(string equipName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return "";

            if (_equipChanelCache.TryGetValue(equipName, out var cached))
                return cached ?? "";

            string ch;
            try
            {
                ch = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{equipName}\",\"Custom1\", 3)");
                ch = (ch ?? "").Trim();
            }
            catch
            {
                ch = "";
            }

            _equipChanelCache[equipName] = ch;
            return ch;
        }

        private async Task<Dictionary<string, string?>> TagReadManyAsync(List<string> tagNames,int maxConcurrency,CancellationToken ct)
        {
            var result = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            using var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));

            var tasks = tagNames.Select(async tag =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var raw = await _ctApiService.TagReadAsync(tag);
                    result[tag] = raw;
                }
                catch
                {
                    result[tag] = null;
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);

            return new Dictionary<string, string?>(result, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<string> ResolveTagNameAsync(string equipName, string equipItem, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            equipName = (equipName ?? "").Trim();
            equipItem = (equipItem ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipName) || string.IsNullOrWhiteSpace(equipItem))
                return "";

            // используем тот же кеш TagInfo, что и для Read/Write параметров
            return await GetTagNameAsync(equipName, equipItem, ct);
        }

        public async Task WriteTagNameAsync(string tagName, string value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            tagName = (tagName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("tagName is empty/Unknown", nameof(tagName));

            value = (value ?? "").Trim();

            await _ctApiService.TagWriteAsync(tagName, value);

            ct.ThrowIfCancellationRequested();
        }
        #endregion
    }

}

