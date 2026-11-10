using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

/// <summary>Проверка выполняется под настоящей учётной записью хоста, а не Maintenance.</summary>
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
            return result with { Message = $"The host cannot open the service for Query/ChangeConfig/Start/Stop. Windows error: {error}." };

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

            result = result with { Service = state };

            var control = await ReadControlAsync(settings, timeout.Token);
            var running = state.PostmasterProcessId > 0 && state.ListenerProcessId > 0;
            var role = !running ? "Stopped" : control.State == "in production" ? "Primary" : control.State == "in archive recovery" ? "Standby" : "Unknown";

            return result with
            {
                Service = state,
                PostgreSqlRole = role,
                ClusterState = control.State,
                SystemIdentifier = control.Identifier,
                Timeline = control.Timeline,
                Message = state.ManagedByService
                    ? "Host access and service ownership confirmed."
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

    /// <summary>Читает роль и идентификатор из локального контрольного файла без SQL-пароля.</summary>
    private static async Task<(string State, string Identifier, int Timeline)> ReadControlAsync(PostgreSqlControlOptions settings, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(settings.BinDirectory, "pg_controldata.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.StartInfo.ArgumentList.Add("-D");
        process.StartInfo.ArgumentList.Add(settings.DataDirectory);
        process.StartInfo.Environment["LC_ALL"] = "C";

        try
        {
            process.Start();

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(process.WaitForExitAsync(ct), output, errors);

            if (process.ExitCode != 0)
                throw new InvalidOperationException("pg_controldata check failed.");

            var values = (await output).Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
            var identifier = values["Database system identifier"];

            if (!Regex.IsMatch(identifier, "^[0-9]+$"))
                throw new InvalidOperationException("Invalid PostgreSQL system identifier.");

            return (values["Database cluster state"], identifier, int.Parse(values["Latest checkpoint's TimeLineID"], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            // Этот процесс только читает контрольный файл и не запускает PostgreSQL.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
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
            var service = OpenServiceW(manager, name, 0x0002 | 0x0004 | 0x0010 | 0x0020);

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