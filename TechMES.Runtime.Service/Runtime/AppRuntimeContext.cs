using System.Reflection;
using Microsoft.Extensions.Options;
using TechMES.Runtime.Service.Settings;

namespace TechMES.Runtime.Service.Runtime;

/// <summary>
/// Реализация runtime-контекста.
/// 
/// Этот класс создаётся один раз при старте Runtime.Service.
/// Он нормализует настройки и даёт остальному коду готовые значения.
/// </summary>
public sealed class AppRuntimeContext : IAppRuntimeContext
{
    public AppRuntimeContext(IOptions<RuntimeOptions> options)
    {
        var runtimeOptions = options.Value;

        MachineName = Environment.MachineName;

        DeviceName = string.IsNullOrWhiteSpace(runtimeOptions.DeviceName)
            ? MachineName
            : runtimeOptions.DeviceName.Trim();

        UserName = string.IsNullOrWhiteSpace(runtimeOptions.UserName)
            ? DeviceName
            : runtimeOptions.UserName.Trim();

        AppVersion = GetAppVersion();
    }

    public string DeviceName { get; }

    public string UserName { get; }

    public string MachineName { get; }

    public string AppVersion { get; }

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var infoVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVersion))
            return infoVersion.Split('+')[0];

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}