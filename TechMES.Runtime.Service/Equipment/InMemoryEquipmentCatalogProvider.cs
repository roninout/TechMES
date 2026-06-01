using TechMES.Application.Equipment;
using TechMES.Contracts.Equipment;

namespace TechMES.Runtime.Service.Equipment;

/// <summary>
/// Временный provider каталога оборудования.
/// 
/// Он нужен, чтобы мы могли построить WEB-экран Equipment
/// до подключения настоящего CtApi.
/// </summary>
public sealed class InMemoryEquipmentCatalogProvider : IEquipmentCatalogProvider
{
    /// <summary>
    /// Тестовый список оборудования, живущий только в памяти процесса.
    /// </summary>
    private readonly List<EquipmentDto> _equipments = [];

    /// <summary>
    /// Защита списка от одновременного доступа нескольких HTTP-запросов.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Заполняет тестовый каталог один раз при старте Runtime.Service.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_equipments.Count > 0)
                return Task.CompletedTask;

            Add("S01.H01.P01", "Pump 01", "Main product pump", "S01", "Motor", EquipmentTypeGroup.Motor, location: "26.1", isFavorite: true);
            Add("S01.H01.P02", "Pump 02", "Reserve product pump", "S01", "Motor", EquipmentTypeGroup.Motor, location: "26.2");
            Add("S01.H01.ATV01", "ATV 01", "Frequency drive for Pump 01", "S01", "ATV", EquipmentTypeGroup.ATV, location: "26.3");

            Add("S01.H01.LT01", "Level transmitter", "Tank level analog input", "S01", "AI", EquipmentTypeGroup.AI, location: "27.1");
            Add("S01.H01.LSH01", "High level switch", "Digital input", "S01", "DI", EquipmentTypeGroup.DI, location: "27.2");
            Add("S01.H01.V01", "Valve 01", "Inlet valve", "S01", "VGA", EquipmentTypeGroup.VGA, location: "28.1");
            Add("S01.H01.VD01", "Diverter 01", "Diverter valve", "S01", "VGD", EquipmentTypeGroup.VGD, location: "28.2");

            Add("S02.H01.P01", "Pump 01", "Station S02 pump", "S02", "Motor", EquipmentTypeGroup.Motor);
            Add("S02.H01.LT01", "Level transmitter", "Station S02 level", "S02", "AI", EquipmentTypeGroup.AI);
            Add("S02.H01.V01", "Valve 01", "Station S02 valve", "S02", "VGA", EquipmentTypeGroup.VGA);

            // Группа и дочерний узел нужны для проверки Tree UI без реального CtApi.
            Add("S03.H01.GRP01", "Group 01", "Equipment group example", "S03", "Equipment", EquipmentTypeGroup.Equipment, isGroup: true);
            Add("S03.H01.P01", "Pump in group", "Child equipment example", "S03", "Motor", EquipmentTypeGroup.Motor, parentName: "S03.H01.GRP01");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Возвращает тестовый каталог, станции и группы типов для фильтров WEB.
    /// </summary>
    public Task<EquipmentListResponse> GetEquipmentListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var items = _equipments
                .OrderBy(x => x.Station)
                .ThenBy(x => x.TypeGroup)
                .ThenBy(x => x.Name)
                .ToList();

            var stations = items
                .Select(x => x.Station)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            var typeGroups = items
                .Select(x => x.TypeGroup)
                .Distinct()
                .OrderBy(x => x.ToString())
                .ToList();

            return Task.FromResult(new EquipmentListResponse
            {
                Equipments = items,
                Stations = stations,
                TypeGroups = typeGroups,
                TotalCount = items.Count
            });
        }
    }

    /// <summary>
    /// Ищет тестовое оборудование по имени.
    /// </summary>
    public Task<EquipmentDto?> GetEquipmentByNameAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var item = _equipments.FirstOrDefault(
                x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(item);
        }
    }

    /// <summary>
    /// Меняет favorite-флаг в in-memory списке.
    /// </summary>
    public Task<EquipmentDto?> SetFavoriteAsync(string name, bool isFavorite, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var items = _equipments
                .Where(x => !x.IsGroup && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var item in items)
            {
                item.IsFavorite = isFavorite;
            }

            return Task.FromResult(items.FirstOrDefault());
        }
    }

    /// <summary>
    /// Добавляет один узел в тестовый каталог и сразу формирует поля дерева.
    /// </summary>
    private void Add(
        string name,
        string displayName,
        string description,
        string station,
        string typeName,
        EquipmentTypeGroup typeGroup,
        bool isGroup = false,
        string? parentName = null,
        string location = "",
        bool isFavorite = false)
    {
        // Даже InMemory provider теперь отдаёт поля дерева.
        // Это помогает проверять WEB-страницу без подключения к CtApi.
        var nodeId = isGroup
            ? $"GRP:{name}"
            : string.IsNullOrWhiteSpace(parentName)
                ? $"EQ:{name}"
                : $"CH:{parentName}:{name}";

        var parentNodeId = string.IsNullOrWhiteSpace(parentName)
            ? "0"
            : $"GRP:{parentName}";

        _equipments.Add(new EquipmentDto
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Location = location,
            Station = station,
            TypeName = typeName,
            TypeGroup = typeGroup,
            IsGroup = isGroup,
            ParentName = parentName,
            IsFavorite = isFavorite,
            NodeId = nodeId,
            ParentNodeId = parentNodeId,
            IsEquipmentChildNode = !string.IsNullOrWhiteSpace(parentName)
        });
    }
}
