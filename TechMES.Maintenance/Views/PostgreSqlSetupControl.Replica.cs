using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using TechMES.Maintenance.Models;
using TechMES.Maintenance.Services;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSetupControl
{
    private sealed record ReplicaPair(string Source, string Target, string SourceProfile, string TargetProfile);
    private sealed record ReplicaCheck(ReplicaPair Pair, string PrimaryId, string TargetId, string Directory);
    private ReplicaCheck? _replicaCheck;

    /// <summary>Фиксирует адреса и отпечатки обоих профилей, чтобы Apply проверял именно ту пару, для которой выполнен Check.</summary>
    private ReplicaPair GetReplicaPair()
    {
        if (_loadFailed || Cards.Count != 2)
            throw new InvalidDataException("Проверьте и сохраните оба профиля PostgreSQL.");

        var source = InitialStandbySource.Text.Trim();
        var target = InitialStandbyTarget.Text.Trim();
        var primary = Cards[0].Profile;
        var secondary = Cards[1].Profile;
        primary.Validate();
        secondary.Validate();
        PostgreSqlNetworkSetupService.ValidateAddress(source);
        PostgreSqlNetworkSetupService.ValidateAddress(target);
        if (source == target || primary.Port != secondary.Port)
            throw new InvalidDataException("Укажите разные адреса и одинаковый порт в профилях PostgreSQL.");

        return new ReplicaPair(source, target, PostgreSqlSetupService.Fingerprint(primary), PostgreSqlSetupService.Fingerprint(secondary));
    }

    private string ReplicaAdmin => string.IsNullOrWhiteSpace(ReplicaAdminUser.Text)
        ? throw new InvalidOperationException("Введите имя администратора PostgreSQL.")
        : ReplicaAdminUser.Text.Trim();

    private string ExpectedPrimaryId => InitialStandbySystemIdentifier.Text.Trim();

    /// <summary>Проверяет локальный Primary, заполняет его ID и показывает готовность WAL и роли репликации.</summary>
    private async void OnInspectReplicaPrimaryClick(object sender, RoutedEventArgs e) =>
        await RunReplicaActionAsync(ReplicaPrimaryResult, async (_, pair) =>
        {
            var profile = Cards[0].Profile;
            PostgreSqlReplicaPreparation.RequireLocal(profile, pair.Source);
            var node = await PostgreSqlReplicaPreparation.CheckNodeAsync("127.0.0.1", profile, ReplicaAdmin, ReplicaAdminPassword.Password);
            PostgreSqlReplicaPreparation.RequireWritablePrimary(node, node.Id);
            InitialStandbySystemIdentifier.Text = node.Id;
            // При подключённой реплике проверка готовности для первой копии уже неприменима.
            var readiness = node.Senders > 0
                ? $"Есть подключения WAL ({node.Senders}). Состояние реплики проверьте на вкладке Secondary."
                : node.WalLevel is "replica" or "logical" && node.FreeSenders >= 2 && node.FreeSlots >= 1
                    ? "WAL: есть свободные отправители и слот для первой реплики."
                    : "Для первого Standby проверьте wal_level (replica или logical), два свободных отправителя и физический слот.";
            return DescribeReplicaNode(node) + $"\n{readiness}\nРоль techmes_repl: {node.ReplicationRole}\npg_hba.conf: {node.HbaFile}";
        });

    /// <summary>Создаёт роль на локальном Primary и помещает новый пароль в поле ввода с маскировкой.</summary>
    private async void OnCreateReplicaRoleClick(object sender, RoutedEventArgs e) =>
        await RunReplicaActionAsync(ReplicaPrimaryResult, async (_, pair) =>
        {
            var password = await PostgreSqlReplicaPreparation.CreateRoleAsync(Cards[0].Profile, pair.Source, ExpectedPrimaryId, ReplicaAdmin, ReplicaAdminPassword.Password);
            if (password is null)
                return "Роль techmes_repl уже настроена. Используйте её действующий пароль.";
            ReplicaPassword.Password = password;
            return "Роль techmes_repl создана. Новый пароль находится в поле выше. Сохраните его для Secondary.";
        });

    /// <summary>Копирует пароль по явному действию оператора для переноса на Secondary.</summary>
    private void OnCopyReplicaPasswordClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReplicaPassword.Password.Length == 0)
                throw new InvalidOperationException("Поле пароля роли пустое.");
            Clipboard.SetText(ReplicaPassword.Password);
            ReplicaPrimaryResult.Text = "Пароль скопирован в буфер обмена.";
        }
        catch (Exception ex)
        {
            ReplicaPrimaryResult.Text = ex.Message;
        }
    }

    /// <summary>Обновляет pg_hba.conf на Primary и показывает путь к резервной копии файла.</summary>
    private async void OnAllowReplicaClick(object sender, RoutedEventArgs e) =>
        await RunReplicaActionAsync(ReplicaPrimaryResult, async (_, pair) =>
        {
            var backup = await PostgreSqlReplicaPreparation.AddReplicationRuleAsync(Cards[0].Profile, pair.Source, pair.Target,
                ExpectedPrimaryId, ReplicaAdmin, ReplicaAdminPassword.Password);
            return (backup is null ? "Правило для techmes_repl уже существует." : $"Правило добавлено. Резервная копия: {backup}")
                + "\nЗатем проверьте физическое подключение на Secondary. Более ранние правила pg_hba.conf сохраняют приоритет.";
        });

    /// <summary>Проверяет существующий Standby или готовность отдельного кластера к созданию первой реплики.</summary>
    private async void OnInspectReplicaSecondaryClick(object sender, RoutedEventArgs e) =>
        await RunReplicaActionAsync(InitialStandbyResult, async (_, pair) =>
        {
            var local = await ReadReplicaTargetAsync(pair);
            if (local.Standby)
            {
                await CheckReplicaSourceFromTargetAsync(pair, checkReplicationLogin: false);
                return VerifyRunningReplica(local, pair) + "\nДалее проверьте контроллеры и Main DB в мастере HA.";
            }

            var primary = await CheckReplicaSourceFromTargetAsync(pair);
            return DescribeReplicaNode(local) + "\n" + DescribeReplicaConnection(primary, local);
        });

    /// <summary>Читает локальный PostgreSQL через loopback и показывает его состояние до обращения к Primary.</summary>
    private async Task<PostgreSqlReplicaPreparation.Node> ReadReplicaTargetAsync(ReplicaPair pair)
    {
        var target = Cards[1].Profile;
        PostgreSqlReplicaPreparation.RequireLocal(target, pair.Target);
        var local = await PostgreSqlReplicaPreparation.CheckNodeAsync("127.0.0.1", target, ReplicaAdmin, ReplicaAdminPassword.Password);
        InitialStandbyResult.Text = DescribeReplicaNode(local);
        return local;
    }

    /// <summary>Проверяет SQL-вход на Primary с Secondary и, при подготовке, вход по протоколу репликации.</summary>
    private async Task<PostgreSqlReplicaPreparation.Node> CheckReplicaSourceFromTargetAsync(ReplicaPair pair, bool checkReplicationLogin = true)
    {
        var source = Cards[0].Profile;
        PostgreSqlReplicaPreparation.Node primary;
        try
        {
            primary = await PostgreSqlReplicaPreparation.CheckNodeAsync(pair.Source, source, ReplicaAdmin, ReplicaAdminPassword.Password);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SQL-вход администратора с Secondary в postgres на {pair.Source}:{source.Port}: {ex.Message}\n"
                + "Если PostgreSQL сообщает об отсутствии правила pg_hba.conf, на Primary нужна отдельная запись для базы postgres, SQL-администратора и IP Secondary /32.", ex);
        }
        PostgreSqlReplicaPreparation.RequireWritablePrimary(primary, ExpectedPrimaryId.Length == 0 ? primary.Id : ExpectedPrimaryId);
        InitialStandbySystemIdentifier.Text = primary.Id;
        if (checkReplicationLogin &&
            await PostgreSqlReplicaPreparation.IdentifyPrimaryAsync(pair.Source, source.Port, ReplicaPassword.Password) != primary.Id)
            throw new InvalidOperationException("Физическое соединение вернуло другой идентификатор кластера.");
        return primary;
    }

    /// <summary>Готовит запуск Check на локальном Secondary и разрешает Apply только после сверки отчёта и обоих ID.</summary>
    private async void OnRunReplicaCheckClick(object sender, RoutedEventArgs e) =>
        await RunReplicaActionAsync(InitialStandbyResult, async (window, pair) =>
        {
            // Новый Check аннулирует предыдущий отчёт, даже если проверка завершится ошибкой.
            _replicaCheck = null;
            ReplicaApply.IsEnabled = false;
            PostgreSqlReplicaPreparation.RequireLocal(Cards[1].Profile, pair.Target);
            PostgreSqlReplicaPreparation.Node? local;
            try
            {
                local = await ReadReplicaTargetAsync(pair);
            }
            catch (NpgsqlException ex) when (ex is not PostgresException)
            {
                // Штатный Check принимает уже остановленный и отключённый экземпляр.
                local = null;
            }

            if (local?.Standby == true)
            {
                await CheckReplicaSourceFromTargetAsync(pair, checkReplicationLogin: false);
                return VerifyRunningReplica(local, pair);
            }
            var primary = await CheckReplicaSourceFromTargetAsync(pair);
            if (local is not null && (local.Id == primary.Id || local.VersionNumber is < 180000 or >= 190000))
                throw new InvalidOperationException("Для создания первого Standby нужен отдельный экземпляр PostgreSQL 18.");

            if (local is not null && MessageBox.Show(window,
                $"{DescribeReplicaNode(local)}\n{DescribeReplicaConnection(primary, local)}\n\n"
                + $"Остановить {Cards[1].Profile.ServiceName} на {pair.Target} и оставить отключённой после успешного Check?",
                "Подготовка Secondary", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return "Проверка отменена. Служба не останавливалась.";

            var folder = new PostgreSqlInitialStandbyService(window.PostgreSqlHostToolsDirectory).Export(Path.GetTempPath(), Cards[0].Profile, pair.Source, Cards[1].Profile, pair.Target, primary.Id);
            ShowReplicaDirectory(folder);
            InitialStandbyResult.Text = "Откроется PowerShell. Введите учётные данные администратора и techmes_repl.";
            var data = await RunReplicaBundleAsync(folder, "Check", local?.Id ?? "");
            var targetId = data.GetProperty("TargetSystemIdentifier").GetString();
            if (data.GetProperty("State").GetString() != "Ready" ||
                data.GetProperty("SystemIdentifier").GetString() != primary.Id ||
                string.IsNullOrEmpty(targetId) || targetId == primary.Id || (local is not null && targetId != local.Id))
                throw new InvalidDataException("Отчёт Check не подтверждает выбранную пару PostgreSQL.");

            _replicaCheck = new ReplicaCheck(pair, primary.Id, targetId, folder);
            ReplicaApply.IsEnabled = true;
            return $"Check: Ready. Primary {pair.Source} · {primary.Id}\nSecondary {pair.Target} · {targetId}\n"
                + $"Слот: {data.GetProperty("Slot").GetString()}. Оценка размера: {data.GetProperty("EstimatedBytes").GetInt64():N0} байт.\n"
                + "Secondary остановлен. Нажмите Apply после проверки, что нужные данные сохранены.";
        });

    /// <summary>Повторно сверяет пару и после подтверждения запускает штатное создание и проверку Standby.</summary>
    private async void OnRunReplicaApplyClick(object sender, RoutedEventArgs e) =>
        await RunReplicaActionAsync(InitialStandbyResult, async (window, pair) =>
        {
            var check = _replicaCheck;
            var profile = Cards[1].Profile;
            PostgreSqlReplicaPreparation.RequireLocal(profile, pair.Target);
            if (check is null || check.Pair != pair || check.PrimaryId != ExpectedPrimaryId)
                throw new InvalidOperationException("Сначала выполните Check для текущих профилей.");

            if (MessageBox.Show(window, $"Источник: {pair.Source} · {check.PrimaryId}\n"
                + $"Назначение: {pair.Target} · {check.TargetId}\nКаталог: {profile.DataDirectory}\n\n"
                + "Копируется весь кластер. Прежний каталог назначения будет сохранён рядом. Создать реплику?",
                "Создать Standby", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return "Apply отменён. Результат Check сохранён.";

            _replicaCheck = null;
            ReplicaApply.IsEnabled = false;
            InitialStandbyResult.Text = "Откроется PowerShell для ввода учётных данных и установки копии.";
            var data = await RunReplicaBundleAsync(check.Directory, "Apply", check.TargetId);
            if (data.GetProperty("State").GetString() != "Initialized")
                throw new InvalidDataException("Отчёт мастера не подтвердил создание Standby.");

            return VerifyRunningReplica(await ReadReplicaTargetAsync(pair), pair)
                + $"\nПрежний каталог: {data.GetProperty("Backup").GetString()}\n"
                + "Выполните проверку контроллеров и подключите Main DB Secondary после подтверждения репликации.";
        });

    /// <summary>Подтверждает, что локальный узел получил ID Primary и принимает WAL от ожидаемого адреса.</summary>
    private string VerifyRunningReplica(PostgreSqlReplicaPreparation.Node local, ReplicaPair pair)
    {
        if (ExpectedPrimaryId.Length == 0 || local.Id != ExpectedPrimaryId || !local.Standby ||
            local.Receiver != "streaming" || local.Sender != pair.Source)
            throw new InvalidOperationException("Локальная реплика пока не подтвердила ID Primary или streaming от выбранного IP.");
        return $"Standby {pair.Target} · ID {local.Id}\nWAL: {local.Sender} → {pair.Target} · streaming · слот {local.Slot}";
    }

    /// <summary>Формирует сводку о роли, каталоге, базах и свободных ресурсах WAL.</summary>
    private static string DescribeReplicaNode(PostgreSqlReplicaPreparation.Node node) =>
        $"PostgreSQL {node.Version} · {(node.Standby ? "Standby" : "Primary")} · ID {node.Id}\n"
        + $"Данные: {node.DataDirectory}\nБазы: {node.Databases}\n"
        + $"WAL: {node.WalLevel} · свободных отправителей: {node.FreeSenders} · слотов: {node.FreeSlots}";

    /// <summary>Показывает результат двух проверок доступа и предупреждает о различии минорных версий.</summary>
    private static string DescribeReplicaConnection(PostgreSqlReplicaPreparation.Node primary, PostgreSqlReplicaPreparation.Node local) =>
        $"SQL и IDENTIFY_SYSTEM подтверждены. Primary PostgreSQL {primary.Version} · ID {primary.Id}."
        + (primary.VersionNumber != local.VersionNumber ? " Перед вводом в эксплуатацию выровняйте минорные версии." : "");

    /// <summary>Запоминает каталог комплекта, чтобы оператор мог открыть отчёты Check и Apply.</summary>
    private void ShowReplicaDirectory(string folder)
    {
        _initialStandbyDirectory = folder;
        InitialStandbyOpenFolder.IsEnabled = true;
    }

    /// <summary>Запускает действие под общей блокировкой Maintenance и выводит ошибку рядом с диагностикой узла.</summary>
    private async Task RunReplicaActionAsync(TextBox output, Func<MainWindow, ReplicaPair, Task<string>> operation)
    {
        try
        {
            if (Window.GetWindow(this) is not MainWindow window)
                return;
            var pair = GetReplicaPair();
            SetCardActionsEnabled(false);
            output.Text = "Выполняется операция…";
            await window.RunPostgreSqlSwitchAsync(async () => output.Text = await operation(window, pair));
        }
        catch (Exception ex)
        {
            var details = output.Text == "Выполняется операция…" ? "" : output.Text;
            output.Text = $"{ex.Message}\n{details}".Trim();
        }
        finally
        {
            SetCardActionsEnabled(true);
        }
    }

    /// <summary>Запускает штатный комплект с повышением прав и читает отчёт выбранного режима.</summary>
    private static async Task<JsonElement> RunReplicaBundleAsync(string folder, string mode, string targetId)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = folder
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(folder, "Setup-Standby.ps1"), "-Mode", mode })
            start.ArgumentList.Add(argument);
        if (targetId.Length > 0)
        {
            start.ArgumentList.Add("-TargetSystemIdentifier");
            start.ArgumentList.Add(targetId);
            if (mode == "Check")
                start.ArgumentList.Add("-PrepareTarget");
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
        await process.WaitForExitAsync();
        var reportPath = Directory.EnumerateFiles(folder, $"result-{mode}-*.json").SingleOrDefault();
        if (reportPath is null)
            throw new InvalidOperationException($"PowerShell завершился без отчёта. Код: {process.ExitCode}. Комплект: {folder}");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        if (process.ExitCode != 0 || !report.RootElement.GetProperty("Success").GetBoolean())
            throw new InvalidOperationException($"{report.RootElement.GetProperty("Error").GetString()}\nОтчёт: {reportPath}");
        return report.RootElement.GetProperty("Data").Clone();
    }
}
