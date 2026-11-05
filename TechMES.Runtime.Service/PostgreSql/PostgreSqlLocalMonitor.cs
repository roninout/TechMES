using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;
using TechMES.Infrastructure.CtApi.Settings;

namespace TechMES.Runtime.Service.PostgreSql;

public interface IPostgreSqlLocalProbe
{
    /// <summary>Проверяет права текущего Runtime и локальный экземпляр без изменения службы.</summary>
    Task<PostgreSqlLocalHealthResponse> CheckAsync(PostgreSqlControlOptions settings, int timeoutSeconds, CancellationToken ct);
}

/// <summary>Проверка выполняется под настоящей учётной записью Runtime, а не Maintenance.</summary>
public sealed class PostgreSqlLocalProbe : IPostgreSqlLocalProbe
{
    /// <summary>Запрашивает права SCM и вызывает только Status-режим опубликованного скрипта.</summary>
    public async Task<PostgreSqlLocalHealthResponse> CheckAsync(PostgreSqlControlOptions settings, int timeoutSeconds, CancellationToken ct)
    {
        var result = new PostgreSqlLocalHealthResponse();

        if (!OperatingSystem.IsWindows())
            return result with { Message = "Local PostgreSQL control requires Windows." };

        using var identity = WindowsIdentity.GetCurrent();

        result = result with { RuntimeIdentity = identity.Name };

        if (!Regex.IsMatch(settings.ServiceName, "^[A-Za-z0-9_.-]+$") || !Path.IsPathFullyQualified(settings.BinDirectory) || !Path.IsPathFullyQualified(settings.DataDirectory) || settings.Port is < 1 or > 65535)
            return result with { Message = "Invalid PostgreSqlControl settings." };

        var error = CheckServiceRights(settings.ServiceName);

        result = result with
        {
            PermissionsOk = error == 0,
            WindowsError = error
        };

        if (error != 0)
            return result with { Message = $"Runtime cannot open the service for Query/Start/Stop. Windows error: {error}." };

        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "Set-PostgreSQL18ServiceControl.ps1");

        if (!File.Exists(script))
            return result with { Message = "Published PostgreSQL status tool was not found." };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", "Status", "-AsJson", "-ServiceName", settings.ServiceName, "-PgBin", settings.BinDirectory, "-DataDirectory", settings.DataDirectory, "-Port", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);

            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);

            if (process.ExitCode != 0)
                return result with { Message = $"PostgreSQL status tool failed. Exit code: {process.ExitCode}." };

            var state = JsonSerializer.Deserialize<PostgreSqlServiceState>(await output, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Empty service status.");

            return result with
            {
                Service = state,
                Message = state.ManagedByService
                    ? "Runtime access and service ownership confirmed."
                    : "Service access confirmed, but PostgreSQL is not managed by this service."
            };
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();

            return result with
            {
                Message = $"Local check failed ({ex.GetType().Name})."
            };
        }
        finally
        {
            // Завершаем только наш диагностический PowerShell, не PostgreSQL.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>Проверяет получение прав без отправки команд запуска или остановки.</summary>
    [SupportedOSPlatform("windows")]
    private static int CheckServiceRights(string name)
    {
        var manager = OpenSCManagerW(null, null, 0x0001);

        if (manager == IntPtr.Zero)
            return Marshal.GetLastWin32Error();

        try
        {
            var service = OpenServiceW(manager, name, 0x0004 | 0x0010 | 0x0020);

            if (service == IntPtr.Zero)
                return Marshal.GetLastWin32Error();

            CloseServiceHandle(service);
            return 0;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>Открывает локальный Service Control Manager.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    /// <summary>Открывает службу с указанным набором прав.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    /// <summary>Освобождает дескриптор SCM или службы.</summary>
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>Хранит согласованный снимок и использует существующий health worker также в Standby.</summary>
public sealed class PostgreSqlLocalMonitor(IOptions<PostgreSqlControlOptions> options, IOptions<CtApiOptions> health, IPostgreSqlLocalProbe probe)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly PostgreSqlControlOptions _settings = options.Value;
    private readonly int _period = Math.Max(5, health.Value.HealthCheckPeriodSeconds);

    private PostgreSqlLocalHealthResponse _state = new()
    {
        Settings = options.Value,
        HealthPeriodSeconds = Math.Max(5, health.Value.HealthCheckPeriodSeconds)
    };

    /// <summary>Без внешних обращений возвращает последний опубликованный результат.</summary>
    public PostgreSqlLocalHealthResponse GetHealth() => Volatile.Read(ref _state);

    /// <summary>Обновляет снимок без Runtime activity lease и исключает параллельные проверки.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct))
            return;

        try
        {
            var result = _settings.Enabled
                ? await probe.CheckAsync(_settings, Math.Clamp(_period - 1, 3, 15), ct)
                : new PostgreSqlLocalHealthResponse { Message = "Local PostgreSQL control diagnostics are disabled." };

            ct.ThrowIfCancellationRequested();

            Volatile.Write(ref _state, result with
            {
                Settings = _settings,
                HealthPeriodSeconds = _period,
                CheckedAtUtc = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _refresh.Release();
        }
    }
}