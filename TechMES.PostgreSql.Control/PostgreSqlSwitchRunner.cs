using System.Diagnostics;
using System.Globalization;
using System.Text;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

public interface IPostgreSqlSwitchRunner
{
    /// <summary>Ожидает завершения скрипта без принудительного прерывания мутации.</summary>
    Task<int> RunAsync(PostgreSqlControlOptions settings, PostgreSqlCommand request, string directory, string credentialPath);
}

public sealed class PostgreSqlSwitchRunner : IPostgreSqlSwitchRunner
{
    /// <summary>Передаёт DPAPI-блок через stdin; паролей нет в аргументах или журнале хоста.</summary>
    public async Task<int> RunAsync(PostgreSqlControlOptions settings, PostgreSqlCommand request, string directory, string credentialPath)
    {
        using var process = new Process { StartInfo = CreateStartInfo(settings, request.Mode, request.From, request.To) };

        foreach (var argument in new[] { "-FenceFile", Path.Combine(directory, "fence.json"), "-OperationId", request.Id, "-ResultFile", Path.Combine(directory, "script-result.json") })
            process.StartInfo.ArgumentList.Add(argument);

        if (request.EmergencyFenceId is { } emergencyFenceId)
        {
            process.StartInfo.ArgumentList.Add("-EmergencyFenceId");
            process.StartInfo.ArgumentList.Add(emergencyFenceId);
        }

        await using var outputFile = new FileStream(Path.Combine(directory, "process-output.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var errorFile = new FileStream(Path.Combine(directory, "process-error.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        process.Start();

        var output = process.StandardOutput.BaseStream.CopyToAsync(outputFile);
        var errors = process.StandardError.BaseStream.CopyToAsync(errorFile);

        await process.StandardInput.WriteLineAsync(Convert.ToBase64String(await File.ReadAllBytesAsync(credentialPath)));

        process.StandardInput.Close();

        // CancellationToken отсутствует: отмена клиента не должна убивать rewind.
        await Task.WhenAll(process.WaitForExitAsync(), output, errors);

        outputFile.Flush(true);
        errorFile.Flush(true);

        return process.ExitCode;
    }

    internal static async Task<bool> IsReadyForFailbackAsync(PostgreSqlControlOptions settings, PostgreSqlSwitchPlan plan, int timeline, int maxReplayLagBytes, PostgreSqlCommandStore store, CancellationToken ct)
    {
        store.ValidateCredentials();
        var credentials = Convert.ToBase64String(await File.ReadAllBytesAsync(store.CredentialPath, ct));
        using var process = new Process { StartInfo = CreateStartInfo(settings, "FailbackReadiness", plan.From, plan.To) };

        foreach (var argument in new[] { "-ExpectedTimeline", timeline.ToString(CultureInfo.InvariantCulture), "-MaxReplayLagBytes", maxReplayLagBytes.ToString(CultureInfo.InvariantCulture) })
            process.StartInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(65));
        var token = timeout.Token;
        process.Start();

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            await process.StandardInput.WriteLineAsync(credentials.AsMemory(), token);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(token), output, errors);

            return process.ExitCode == 0 && (await output).Trim() == "READY";
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(PostgreSqlControlOptions settings, string mode, string from, string to)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PostgreSQL switching requires Windows.");

        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "Switch-PostgreSQL18.ps1");

        if (!File.Exists(script))
            throw new FileNotFoundException("The published switch tool is missing.");

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", mode, "-From", from, "-To", to, "-PgBin", settings.BinDirectory, "-DataDirectory", settings.DataDirectory, "-ServiceName", settings.ServiceName, "-Port", settings.Port.ToString(CultureInfo.InvariantCulture), "-CredentialInput", "-ExpectedSystemIdentifier", settings.ExpectedSystemIdentifier })
            start.ArgumentList.Add(argument);

        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);

        return start;
    }
}
