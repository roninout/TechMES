using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    /// <summary>Проверяет доступ по существующему локальному адресу Runtime.</summary>
    public Task CheckPostgreSqlControlAccessAsync() => TypedAppSettings.Redundancy.CheckAsync(GetRuntimeDiagnosticsBaseUrl());

    /// <summary>Находит рабочий appsettings по реальному ImagePath установленной службы.</summary>
    private static string? GetInstalledAppsettingsPath(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);

        if (key is null)
            return null;

        var image = Environment.ExpandEnvironmentVariables(key.GetValue("ImagePath") as string ?? "");
        var match = Regex.Match(image, "^\\s*(?:\"([^\"]+\\.exe)\"|(.+?\\.exe))(?:\\s|$)", RegexOptions.IgnoreCase);

        if (!match.Success)
            throw new InvalidOperationException($"Не удалось прочитать ImagePath службы {serviceName}.");

        var exe = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

        if (!string.Equals(Path.GetFileName(exe), serviceName + ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Служба {serviceName} использует нестандартный запуск: {exe}.");

        return Path.Combine(
            Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("Пустой каталог службы."),
            "appsettings.json");
    }
}