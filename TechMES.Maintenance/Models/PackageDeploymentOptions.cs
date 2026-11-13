using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TechMES.Maintenance.Models;

/// <summary>Общий архив и два независимых профиля локального развёртывания.</summary>
public sealed class PackageDeploymentOptions
{
    public string ArchivePath { get; set; } = "";
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TechMESDeployments");
    public ServerDeploymentProfile Primary { get; set; } = new() { Name = "Primary", ComputerName = "192.168.1.118", InstallRoot = @"C:\TechMES", ShareRoot = @"\\192.168.1.118\C$\TechMESUpdates", UpdateMaintenance = true };
    public ServerDeploymentProfile Secondary { get; set; } = new() { Name = "Secondary", ComputerName = "192.168.1.132", InstallRoot = @"D:\TechMES", ShareRoot = @"\\192.168.1.132\TechMESUpdates" };

    [JsonIgnore] public string LoadError { get; set; } = "";
    [JsonIgnore] public ServerDeploymentProfile[] Profiles => [Primary, Secondary];

    /// <summary>Проверяет оба профиля; сохранение доступно ещё до сборки ZIP.</summary>
    public void Validate(bool requireArchive)
    {
        if (Primary is null || Secondary is null || Primary.Name != "Primary" || Secondary.Name != "Secondary")
            throw new InvalidDataException("Нужны профили Primary и Secondary.");
        foreach (var profile in Profiles)
            profile.Validate();
        if (Primary.ComputerName.Equals(Secondary.ComputerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Укажите разные компьютеры Primary и Secondary.");
        if (requireArchive && (!File.Exists(ArchivePath) || !Path.GetExtension(ArchivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Выберите готовый ZIP TechMES.");
        if (requireArchive && (string.IsNullOrWhiteSpace(OutputFolder) || !Path.IsPathFullyQualified(OutputFolder) || OutputFolder.StartsWith(@"\\", StringComparison.Ordinal)))
            throw new InvalidDataException("Укажите локальную папку подготовки комплекта.");
    }
}

/// <summary>Настройки, которые получит локальный скрипт выбранного сервера.</summary>
public sealed class ServerDeploymentProfile
{
    public string Name { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string InstallRoot { get; set; } = @"D:\TechMES";
    public string ShareRoot { get; set; } = "";
    public string RuntimeStatusUrl { get; set; } = "http://127.0.0.1:5101/api/runtime/status";
    public bool UpdateRuntime { get; set; } = true;
    public bool UpdateCalc { get; set; } = true;
    public bool UpdateWeb { get; set; } = true;
    /// <summary>Host обновляется отдельным выбором, чтобы старые профили не устанавливали новую службу неожиданно.</summary>
    public bool UpdateHost { get; set; }
    public bool UpdateMaintenance { get; set; }

    [JsonIgnore] public string[] Components => new[] { UpdateRuntime ? "runtime" : null, UpdateCalc ? "calc" : null, UpdateWeb ? "web" : null, UpdateHost ? "host" : null, UpdateMaintenance ? "maintenance" : null }.OfType<string>().ToArray();

    /// <summary>Проверяет локальный путь, адрес проверки и выбранные компоненты.</summary>
    public void Validate()
    {
        if (!Regex.IsMatch(ComputerName ?? "", @"^[A-Za-z0-9][A-Za-z0-9_.-]*$"))
            throw new InvalidDataException($"{Name}: укажите имя или IPv4 компьютера.");
        var path = InstallRoot ?? "";
        if (!Regex.IsMatch(path, @"^[A-Za-z]:\\[^\\]") || path[3..].Split('\\').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '<', '>', '"', '|', '?', '*', '/']) >= 0 || part.Any(char.IsControl)))
            throw new InvalidDataException($"{Name}: укажите путь установки, например D:\\TechMES, без завершающего слеша.");
        if (Components.Length == 0)
            throw new InvalidDataException($"{Name}: выберите хотя бы один компонент.");
        if (!Uri.TryCreate(RuntimeStatusUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new InvalidDataException($"{Name}: URL Runtime должен указывать на localhost этого сервера.");
    }
}
