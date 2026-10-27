using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechMES.Contracts.Users;

/// <summary>Состояние пользователя. Имена прежних полей совместимы с user-state.json.</summary>
public sealed class UserStateDto
{
    public bool IsDarkTheme { get; set; }
    public string LastEquipName { get; set; } = "";
    public string LastNodeId { get; set; } = "";
    public DateTimeOffset? DbDate { get; set; }
    public int SelectedTab { get; set; } = 1;
    public string LastPage { get; set; } = "";
    public string SelectedStation { get; set; } = "All";
    public int SelectedTypeFilter { get; set; }
    public Dictionary<string, string> LastEquipmentsByFilter { get; set; } = [];
    public int? QrCameraIndex { get; set; }

    // Не теряем поля настроек, которые добавят другие модули или следующая версия.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}