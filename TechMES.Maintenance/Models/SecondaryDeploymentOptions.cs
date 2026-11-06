using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TechMES.Maintenance.Models;

/// <summary>
/// Настройки единственной Maintenance для обновления Secondary.
/// </summary>
public sealed class SecondaryDeploymentOptions
{
    public string ArchivePath { get; set; } = "";
    public string ComputerName { get; set; } = "192.168.1.131";
    public string ShareRoot { get; set; } = @"\\192.168.1.131\D$\TechMESUpdates";
    public string StageRoot { get; set; } = @"D:\TechMESUpdates";
    public string InstallRoot { get; set; } = @"D:\TechMES";
    public string CredentialPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TechMES", "secondary.credential.xml");
    public string RuntimeStatusUrl { get; set; } = "http://127.0.0.1:5101/api/runtime/status";
    public bool UpdateRuntime { get; set; } = true;
    public bool UpdateCalc { get; set; } = true;
    public bool UpdateWeb { get; set; } = true;

    [JsonIgnore]
    public string LoadError { get; set; } = "";

    /// <summary>
    /// Возвращает выбранные компоненты для параметра PowerShell.
    /// </summary>
    [JsonIgnore]
    public string Components => string.Join(",", new[] { UpdateRuntime ? "runtime" : null, UpdateCalc ? "calc" : null, UpdateWeb ? "web" : null }.Where(value => value is not null));

    /// <summary>
    /// Проверяет форму до запуска процесса и сетевых операций.
    /// Сохранить сетевые настройки можно до создания архива и файла учётной записи.
    /// </summary>
    public void Validate(bool saving = false)
    {
        if (!saving && LoadError.Length != 0)
            throw new InvalidOperationException(LoadError + " Исправьте поля и сохраните профиль.");

        if (!saving && (!File.Exists(ArchivePath) || !string.Equals(Path.GetExtension(ArchivePath), ".zip", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Выберите готовый TechMES_Update_*.zip.");

        if (!saving && CredentialPath.Length != 0 && !File.Exists(CredentialPath))
            throw new InvalidOperationException("Создайте зашифрованный файл учётной записи Secondary или очистите поле для текущей учётной записи Windows.");

        var match = Regex.Match(ShareRoot, @"^\\\\([^\\/]+)\\[^\\/]+");

        if (!Regex.IsMatch(ComputerName, @"^[A-Za-z0-9_.-]+$") || !match.Success || !string.Equals(match.Groups[1].Value, ComputerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Имя Secondary должно совпадать с сервером в UNC-пути.");

        foreach (var path in new[] { StageRoot, InstallRoot })
            if (!Regex.IsMatch(path, @"^[A-Za-z]:\\.+$") || path.Split('\\').Any(part => part is "." or ".."))
                throw new InvalidOperationException(@"Укажите локальные пути Secondary, например D:\TechMES и D:\TechMESUpdates.");

        var stage = StageRoot.Replace('\\', '/').TrimEnd('/');
        var install = InstallRoot.Replace('\\', '/').TrimEnd('/');

        if (stage.Equals(install, StringComparison.OrdinalIgnoreCase) || stage.StartsWith(install + "/", StringComparison.OrdinalIgnoreCase) || install.StartsWith(stage + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Папки приложений и пакетов должны быть раздельными.");

        if (Components.Length == 0)
            throw new InvalidOperationException("Выберите хотя бы одну службу.");

        if (!Uri.TryCreate(RuntimeStatusUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("URL проверки должен указывать на localhost Secondary.");
    }

    /// <summary>
    /// Передаёт значения отдельными аргументами.
    /// Пароль в командную строку не попадает.
    /// </summary>
    public IReadOnlyList<string> GetArguments(string scriptPath, string logPath)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-ComputerName", ComputerName, "-ArchivePath", ArchivePath, "-ShareRoot", ShareRoot, "-StageRoot", StageRoot, "-InstallRoot", InstallRoot, "-RuntimeStatusUrl", RuntimeStatusUrl, "-Components", Components, "-LogPath", logPath };

        if (CredentialPath.Length != 0)
        {
            args.Add("-CredentialPath");
            args.Add(CredentialPath);
        }

        return args;
    }
}