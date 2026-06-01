using TechMES.Application.Equipment;
using TechMES.Application.Info;
using TechMES.Contracts.Equipment;
using TechMES.Runtime.Service.Runtime;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// HTTP API каталога оборудования.
/// Данные оборудования берутся из IEquipmentCatalogProvider, а пользовательские признаки
/// вроде favorites и счетчиков Info накладываются из IEquipmentInfoStore.
/// </summary>
public static class EquipmentEndpoints
{
    /// <summary>
    /// Подключает endpoints списка, карточки оборудования и favorite-флага.
    /// </summary>
    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/equipment", GetEquipmentListAsync);
        app.MapGet("/api/equipment/{name}", GetEquipmentByNameAsync);
        app.MapPut("/api/equipment/{name}/favorite", SetEquipmentFavoriteAsync);

        return app;
    }

    /// <summary>
    /// Возвращает полный каталог оборудования для дерева/списка WEB.
    /// Перед ответом добавляет favorite-флаги текущего устройства и счетчики Info-модуля.
    /// </summary>
    private static async Task<IResult> GetEquipmentListAsync(
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentInfoStore infoStore,
        IAppRuntimeContext runtime,
        string? deviceName,
        CancellationToken ct)
    {
        var response = CloneResponse(await equipmentCatalog.GetEquipmentListAsync(ct));
        await ApplyFavoriteFlagsAsync(response, infoStore, ResolveDeviceName(deviceName, runtime), ct);
        await ApplyInfoSummariesAsync(response, infoStore, ct);

        return Results.Ok(response);
    }

    /// <summary>
    /// Возвращает один equipment node по имени и добавляет его favorite-флаг.
    /// </summary>
    private static async Task<IResult> GetEquipmentByNameAsync(
        string name,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentInfoStore infoStore,
        IAppRuntimeContext runtime,
        string? deviceName,
        CancellationToken ct)
    {
        var equipment = CloneEquipment(await equipmentCatalog.GetEquipmentByNameAsync(name, ct));
        if (equipment is not null)
            await ApplyFavoriteFlagAsync(equipment, infoStore, ResolveDeviceName(deviceName, runtime), ct);

        return equipment is null
            ? Results.NotFound()
            : Results.Ok(equipment);
    }

    /// <summary>
    /// Сохраняет favorite в пользовательском PostgreSQL-хранилище и синхронизирует
    /// in-memory/CtApi представление, чтобы текущий ответ сразу отразил новое состояние.
    /// </summary>
    private static async Task<IResult> SetEquipmentFavoriteAsync(
        string name,
        EquipmentFavoriteRequest request,
        IEquipmentCatalogProvider equipmentCatalog,
        IEquipmentInfoStore infoStore,
        IAppRuntimeContext runtime,
        string? deviceName,
        CancellationToken ct)
    {
        var resolvedDeviceName = ResolveDeviceName(deviceName, runtime);
        await infoStore.SetFavoriteAsync(name, request.IsFavorite, resolvedDeviceName, ct);

        var equipment = await equipmentCatalog.SetFavoriteAsync(name, request.IsFavorite, ct);
        if (equipment is not null)
            equipment.IsFavorite = request.IsFavorite;

        return equipment is null
            ? Results.NotFound()
            : Results.Ok(equipment);
    }

    /// <summary>
    /// Создает копию ответа каталога, чтобы endpoint мог безопасно добавить UI-поля
    /// и не мутировать внутренний cache provider-а.
    /// </summary>
    private static EquipmentListResponse CloneResponse(EquipmentListResponse response)
    {
        return new EquipmentListResponse
        {
            Equipments = response.Equipments.Select(CloneEquipment).Where(x => x is not null).Select(x => x!).ToList(),
            Stations = response.Stations.ToList(),
            TypeGroups = response.TypeGroups.ToList(),
            TotalCount = response.TotalCount
        };
    }

    /// <summary>
    /// Копирует один equipment node без ссылок на объект из внутреннего кэша каталога.
    /// </summary>
    private static EquipmentDto? CloneEquipment(EquipmentDto? equipment)
    {
        if (equipment is null)
            return null;

        return new EquipmentDto
        {
            Name = equipment.Name,
            DisplayName = equipment.DisplayName,
            Description = equipment.Description,
            Location = equipment.Location,
            Station = equipment.Station,
            TypeName = equipment.TypeName,
            TypeGroup = equipment.TypeGroup,
            IsGroup = equipment.IsGroup,
            ParentName = equipment.ParentName,
            IsFavorite = equipment.IsFavorite,
            NodeId = equipment.NodeId,
            ParentNodeId = equipment.ParentNodeId,
            IsEquipmentChildNode = equipment.IsEquipmentChildNode,
            PhotoCount = equipment.PhotoCount,
            InstructionCount = equipment.InstructionCount,
            SchemeCount = equipment.SchemeCount,
            NoteCount = equipment.NoteCount
        };
    }

    /// <summary>
    /// Проставляет IsFavorite для всех негрупповых элементов каталога.
    /// Favorites хранятся по имени устройства/клиента.
    /// </summary>
    private static async Task ApplyFavoriteFlagsAsync(
        EquipmentListResponse response,
        IEquipmentInfoStore infoStore,
        string deviceName,
        CancellationToken ct)
    {
        var favorites = await infoStore.GetFavoriteEquipNamesAsync(deviceName, ct);
        var favoriteSet = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);

        foreach (var equipment in response.Equipments)
        {
            if (equipment.IsGroup)
                continue;

            equipment.IsFavorite = favoriteSet.Contains(equipment.Name);
        }
    }

    /// <summary>
    /// Добавляет к equipment node счетчики Info-модуля: фото, PDF, схемы и notes.
    /// Эти счетчики используются в списке оборудования и footer.
    /// </summary>
    private static async Task ApplyInfoSummariesAsync(
        EquipmentListResponse response,
        IEquipmentInfoStore infoStore,
        CancellationToken ct)
    {
        var summaries = await infoStore.GetSummariesAsync(
            response.Equipments.Select(x => x.Name),
            ct);

        var summaryByName = summaries.ToDictionary(
            x => x.EquipName,
            StringComparer.OrdinalIgnoreCase);

        foreach (var equipment in response.Equipments)
        {
            if (!summaryByName.TryGetValue(equipment.Name, out var summary))
                continue;

            equipment.PhotoCount = summary.PhotoCount;
            equipment.InstructionCount = summary.InstructionCount;
            equipment.SchemeCount = summary.SchemeCount;
            equipment.NoteCount = summary.NoteCount;
        }
    }

    /// <summary>
    /// Проставляет favorite-флаг для одного оборудования.
    /// </summary>
    private static async Task ApplyFavoriteFlagAsync(
        EquipmentDto equipment,
        IEquipmentInfoStore infoStore,
        string deviceName,
        CancellationToken ct)
    {
        if (equipment.IsGroup)
            return;

        var favorites = await infoStore.GetFavoriteEquipNamesAsync(deviceName, ct);
        equipment.IsFavorite = favorites.Contains(equipment.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Определяет имя клиента. Если WEB не передал deviceName, используем DeviceName Runtime.Service.
    /// </summary>
    private static string ResolveDeviceName(string? deviceName, IAppRuntimeContext runtime)
    {
        return string.IsNullOrWhiteSpace(deviceName)
            ? runtime.DeviceName
            : deviceName.Trim();
    }

    private sealed record EquipmentFavoriteRequest(bool IsFavorite);
}
