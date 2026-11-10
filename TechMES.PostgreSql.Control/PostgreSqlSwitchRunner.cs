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
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PostgreSQL switching requires Windows.");

        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "Switch-PostgreSQL18.ps1");

        if (!File.Exists(script))
            throw new FileNotFoundException("The published switch tool is missing.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            }
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", request.Mode, "-From", request.From, "-To", request.To, "-FenceFile", Path.Combine(directory, "fence.json"), "-PgBin", settings.BinDirectory, "-DataDirectory", settings.DataDirectory, "-ServiceName", settings.ServiceName, "-Port", settings.Port.ToString(CultureInfo.InvariantCulture), "-CredentialInput", "-OperationId", request.Id, "-ExpectedSystemIdentifier", settings.ExpectedSystemIdentifier, "-ResultFile", Path.Combine(directory, "script-result.json") })
            process.StartInfo.ArgumentList.Add(argument);

        foreach (var key in process.StartInfo.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            process.StartInfo.Environment.Remove(key);

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
}