using Microsoft.Win32;
using System.Windows;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    /// <summary>Использует опубликованный инструмент Maintenance либо тот же файл из исходников.</summary>
    public string GetPostgreSqlSetupTool() => GetDeploymentTool("Set-PostgreSQL18ServiceControl.ps1");

    /// <summary>Использует тот же каталог опубликованных инструментов для проверки и экспорта контроллера.</summary>
    public string PostgreSqlHostToolsDirectory => Path.GetDirectoryName(GetDeploymentTool("Prepare-TechMESPostgreSqlController.ps1"))!;

    internal async Task RunLocalPostgreSqlNetworkSetupAsync(string folder)
    {
        if (_deploymentOperationRunning)
            throw new InvalidOperationException("Дождитесь завершения текущей операции Maintenance.");

        _deploymentOperationRunning = true;
        IsEnabled = false;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(folder, "Setup-Network.ps1") })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var errorPath = Path.Combine(folder, "error.txt");
                var reason = File.Exists(errorPath) ? File.ReadAllText(errorPath).Trim() : $"Код PowerShell {process.ExitCode}.";
                throw new InvalidOperationException($"Подготовка сертификата Maintenance остановлена.\n{reason}\nКомплект: {folder}");
            }
        }
        finally
        {
            _deploymentOperationRunning = false;
            IsEnabled = true;
        }
    }

    /// <summary>Копирует комплект вне UI-потока и сериализует операцию с deployment.</summary>
    public async Task<string> ExportPostgreSqlHostSetupAsync(PostgreSqlSetupProfile profile, string outputDirectory, bool includePackage = true, PostgreSqlFailbackSetup? failback = null)
    {
        if (_deploymentOperationRunning)
            throw new InvalidOperationException("Дождитесь завершения текущей операции Maintenance.");

        var tools = PostgreSqlHostToolsDirectory;
        _deploymentOperationRunning = true;
        IsEnabled = false;

        try
        {
            return await Task.Run(() => new PostgreSqlHostSetupService().Export(profile, outputDirectory, tools, includePackage, failback));
        }
        finally
        {
            _deploymentOperationRunning = false;
            IsEnabled = true;
        }
    }

    /// <summary>Запускает проверку или подготовку контроллера на текущем компьютере.</summary>
    public async Task<PostgreSqlHostSetupReport> RunPostgreSqlHostSetupAsync(PostgreSqlSetupProfile profile, bool prepare, bool includePackage = true, PostgreSqlFailbackSetup? failback = null)
    {
        if (_deploymentOperationRunning)
            throw new InvalidOperationException("Дождитесь завершения текущей операции Maintenance.");

        profile.ValidateHost();

        if (!PostgreSqlSetupService.IsLocal(profile))
            throw new InvalidOperationException("Этот профиль относится к " + profile.ComputerName + ". Экспортируйте комплект и запустите его на сервере БД.");

        _deploymentOperationRunning = true;
        IsEnabled = false;

        try
        {
            return await new PostgreSqlHostSetupService().RunLocalAsync(profile, PostgreSqlHostToolsDirectory, prepare, includePackage, failback);
        }
        finally
        {
            _deploymentOperationRunning = false;
            IsEnabled = true;
        }
    }

    public bool CanBuildPostgreSqlAgent => File.Exists(Path.Combine(_repositoryRoot.FullName, "TechMES.PostgreSql.Agent", "TechMES.PostgreSql.Agent.csproj"));

    /// <summary>Запускает штатную сборку Agent и возвращает путь готового пакета.</summary>
    public async Task<string> BuildPostgreSqlAgentAsync(string outputDirectory)
    {
        if (_deploymentOperationRunning)
            throw new InvalidOperationException("Дождитесь завершения текущей операции Maintenance.");

        if (!CanBuildPostgreSqlAgent)
            throw new InvalidOperationException("Для сборки Agent выберите репозиторий TechMES в Settings → Server.");

        _deploymentOperationRunning = true;
        IsEnabled = false;

        try
        {
            const string packageMarker = "PACKAGE_ROOT=";
            var output = new ConcurrentQueue<string>();
            var arguments = new[] { "-RepositoryRoot", _repositoryRoot.FullName, "-OutputRoot", outputDirectory, "-NoArchive" };

            var exitCode = await RunDeploymentToolAsync("Build-TechMESPostgreSqlAgent.ps1", arguments, output.Enqueue);
            var package = output.LastOrDefault(line => line.StartsWith(packageMarker, StringComparison.Ordinal));

            if (exitCode != 0 || package is null)
                throw new InvalidOperationException("Сборка Agent не завершена.\n" + string.Join("\n", output.TakeLast(10)));

            return package[packageMarker.Length..];
        }
        finally
        {
            _deploymentOperationRunning = false;
            IsEnabled = true;
        }
    }

    /// <summary>Сериализует подготовку БД с deployment и оставляет автономную консоль работать до завершения.</summary>
    public async Task<PostgreSqlSetupReport?> RunPostgreSqlSetupAsync(PostgreSqlSetupProfile profile, bool prepare)
    {
        if (_deploymentOperationRunning)
            throw new InvalidOperationException("Дождитесь завершения текущей операции Maintenance.");

        profile.Validate();

        if (!PostgreSqlSetupService.IsLocal(profile))
            throw new InvalidOperationException("Профиль относится к " + profile.ComputerName + ". Экспортируйте скрипт и запустите его на этом сервере БД.");

        if (prepare && MessageBox.Show(this, $"Компьютер: {Environment.MachineName}\nСлужба: {profile.ServiceName}\nДанные: {profile.DataDirectory}\n\nAdoptService при необходимости корректно остановит PostgreSQL и запустит его через существующую службу. Подключения к БД будут прерваны.\n\nВыполнить?", "Подготовка PostgreSQL", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return null;

        _deploymentOperationRunning = true;
        IsEnabled = false;

        try
        {
            return await new PostgreSqlSetupService().RunLocalAsync(profile, GetPostgreSqlSetupTool(), prepare);
        }
        finally
        {
            _deploymentOperationRunning = false;
            IsEnabled = true;
        }
    }

    /// <summary>Перед первоначальной подготовкой локальная БД должна подтвердить владение службой свежей проверкой.</summary>
    public async Task<bool> CheckPostgreSqlBeforeInitialPreparationAsync()
    {
        try
        {
            var profiles = new PostgreSqlSetupService().LoadProfiles().Where(PostgreSqlSetupService.IsLocal).ToArray();

            if (profiles.Length == 0)
            {
                using var installed = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\postgresql-x64-18");

                if (installed is not null)
                    throw new InvalidOperationException("Обнаружен локальный PostgreSQL. В General → PostgreSQL настройте профиль этого компьютера и выполните проверку.");

                return true;
            }

            if (profiles.Length != 1)
                throw new InvalidOperationException("Несколько профилей PostgreSQL указывают на текущий компьютер. Исправьте General → PostgreSQL.");

            var report = await RunPostgreSqlSetupAsync(profiles[0], false);

            if (report?.Ready != true)
                throw new InvalidOperationException("Подготовка PostgreSQL не подтверждена. General → PostgreSQL: проверьте профиль и выполните AdoptService. " + report?.Message);

            return true;
        }
        catch (Exception ex)
        {
            AppendServerLog("PostgreSQL preparation: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Первоначальная подготовка остановлена", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
