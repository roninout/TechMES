using Microsoft.Win32;
using System.Windows;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance;

public partial class MainWindow
{
    /// <summary>Использует опубликованный инструмент Maintenance либо тот же файл из исходников.</summary>
    public string GetPostgreSqlSetupTool() => GetDeploymentTool("Set-PostgreSQL18ServiceControl.ps1");

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