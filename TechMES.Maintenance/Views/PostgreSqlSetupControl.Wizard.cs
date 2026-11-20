using System.IO;
using System.Windows;
using System.Windows.Controls;
using TechMES.Maintenance.Services;
using TechMES.PostgreSql.Control;

namespace TechMES.Maintenance.Views;

public partial class PostgreSqlSetupControl
{
    private bool _wizardPairVerified;

    private void OnWizardProfilesClick(object sender, RoutedEventArgs e) => ActionsPanel.SelectedItem = ProfileTab;

    private void OnWizardPreparationClick(object sender, RoutedEventArgs e) => ActionsPanel.SelectedItem = PreparationTab;

    private void OnWizardRuntimeUrlChanged(object sender, TextChangedEventArgs e)
    {
        _wizardPairVerified = false;
        if (WizardResult is not null)
            WizardResult.Text = "После изменения URL выполните проверку пары.";
        UpdateWizardPolicyButton();
    }

    private void OnWizardPolicyChanged(object sender, RoutedEventArgs e) => UpdateWizardPolicyButton();

    private async void OnCheckWizardClick(object sender, RoutedEventArgs e)
    {
        _wizardPairVerified = false;
        UpdateWizardPolicyButton();
        SetCardActionsEnabled(false);
        WizardResult.Text = "Проверяем контроллеры и поток WAL…";

        try
        {
            await RefreshCardsAsync(manageActions: false);
            var (primary, standby) = GetWizardPair();
            var (cluster, main) = await PostgreSqlWizardDiagnostics.ReadAsync(WizardRuntimeUrl.Text);
            var error = PostgreSqlWizardDiagnostics.Check(cluster, main, primary.NodeAddress, standby.NodeAddress, primary.Local.SystemIdentifier!, DateTimeOffset.UtcNow);
            var receiver = new[] { cluster.Primary, cluster.Secondary }.FirstOrDefault(node => node.ServerAddress == standby.NodeAddress);
            var lag = receiver?.ReplayBacklogBytes is { } bytes ? $"{bytes:N0} байт" : "—";

            WizardResult.Text = $"Primary: {primary.NodeAddress} · {ControllerName(primary.Local.HostKind)}\n"
                + $"Standby: {standby.NodeAddress} · {ControllerName(standby.Local.HostKind)}\n"
                + $"WAL: {receiver?.SenderHost ?? "—"} → {standby.NodeAddress} · {receiver?.ReceiverStatus ?? "—"} · слот {receiver?.SlotName ?? "—"} · отставание применения {lag}\n"
                + (error ?? $"Main DB: запись на {primary.NodeAddress} подтверждена диагностикой Runtime.");
            _wizardPairVerified = error is null;
        }
        catch (Exception ex)
        {
            WizardResult.Text = ex.Message;
            if (Cards.Any(card => card.LiveNode is null))
                WizardResult.Text += "\nЕсли mTLS ещё не настроен, подготовьте сетевую связь обоих контроллеров и клиент Maintenance штатными скриптами.";
        }
        finally
        {
            SetCardActionsEnabled(true);
            UpdateWizardPolicyButton();
        }
    }

    private (PostgreSqlRemoteStatus Primary, PostgreSqlRemoteStatus Standby) GetWizardPair()
    {
        foreach (var card in Cards)
            card.Profile.ValidateHost();

        var nodes = Cards.Select(card => card.LiveNode).OfType<PostgreSqlRemoteStatus>().ToArray();
        if (nodes.Length != 2 || nodes[0].NodeAddress == nodes[1].NodeAddress)
            throw new InvalidDataException("Нужны свежие ответы двух настроенных контроллеров. "
                + string.Join(" ", Cards.Select(card => card.NetworkError).Distinct()));

        if (nodes.Any(node => node.ActivePlan is not null))
            throw new InvalidDataException("Есть активная операция PostgreSQL. Её состояние доступно во вкладке Switching.");

        var now = DateTimeOffset.UtcNow;
        foreach (var card in Cards)
        {
            var local = card.Live!;
            var profile = card.Profile;
            if (!PostgreSqlWizardDiagnostics.IsFresh(local.CheckedAtUtc, local.HealthPeriodSeconds, now))
                throw new InvalidDataException($"Снимок узла {card.Address} устарел. Проверьте пару повторно.");

            if (!local.OwnsController || !local.PermissionsOk || !local.Settings.CommandsEnabled || local.ControllerState != "Monitoring"
                || local.Service is not { ManagedByService: true, ServiceState: "Running" })
                throw new InvalidDataException($"Узел {card.Address}: работа службы, владение или разрешение команд не подтверждены.");

            if ((profile.HostMode != "Auto" && profile.HostMode != local.HostKind)
                || !string.Equals(profile.ServiceName, local.Settings.ServiceName, StringComparison.OrdinalIgnoreCase)
                || profile.Port != local.Settings.Port || !SameWizardPath(profile.BinDirectory, local.Settings.BinDirectory)
                || !SameWizardPath(profile.DataDirectory, local.Settings.DataDirectory))
                throw new InvalidDataException($"Узел {card.Address}: профиль отличается от действующей конфигурации.");
        }

        var primary = nodes.FirstOrDefault(node => node.Local.PostgreSqlRole == "Primary");
        var standby = nodes.FirstOrDefault(node => node.Local.PostgreSqlRole == "Standby");
        if (primary is null || standby is null || string.IsNullOrEmpty(primary.Local.SystemIdentifier)
            || nodes.Any(node => node.Local.SystemIdentifier != primary.Local.SystemIdentifier
                || node.Local.Settings.ExpectedSystemIdentifier != primary.Local.SystemIdentifier))
            throw new InvalidDataException("Нужны Primary и Standby одного кластера PostgreSQL.");

        return (primary, standby);
    }

    private static bool SameWizardPath(string left, string right) =>
        string.Equals(left.Replace('/', '\\'), right.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private void UpdateWizardPolicyButton()
    {
        if (WizardApply is null || WizardPreferredPrimary is null || WizardEnableFailback is null)
            return;

        var selected = ServerCards.SelectedItem as ServerCard;
        var preferred = WizardPreferredPrimary.SelectedItem as ServerCard;
        var enabled = WizardEnableFailback.IsChecked == true;
        var canEnable = preferred is not null && !ReferenceEquals(selected, preferred) && PreferredNodeIsPassive(preferred);

        WizardApply.IsEnabled = _wizardPairVerified && selected is not null && preferred is not null
            && (!enabled || canEnable);
        WizardPolicyHint.Text = enabled
            ? "Чтобы включить автовозврат, выберите второй узел. Сначала примените отключение на предпочтительном Primary и загрузите результат, если использовали комплект."
            : "Для настройки пары сначала выберите предпочтительный узел и в списке, и в верхней карточке. Примените отключение автовозврата на нём.";
    }

    private static bool PreferredNodeIsPassive(ServerCard preferred) =>
        preferred.Report is { Ready: true, Failback: { Enabled: false } policy }
        && preferred.LiveNode is { } node && policy.NodeAddress == node.NodeAddress
        && policy.PreferredPrimary == node.NodeAddress && policy.SystemIdentifier == node.Local.SystemIdentifier;

    private async void OnWizardApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var (primary, standby) = GetWizardPair();
            var card = SelectedCard();
            if (WizardPreferredPrimary.SelectedItem is not ServerCard preferred || preferred.LiveNode is null)
                throw new InvalidDataException("Выберите предпочтительный Primary из проверенной пары.");

            var enabled = WizardEnableFailback.IsChecked == true;
            if (enabled && (ReferenceEquals(card, preferred) || !PreferredNodeIsPassive(preferred)))
                throw new InvalidDataException("Перед включением подтвердите отключение автовозврата на предпочтительном Primary.");

            var node = card.LiveNode!.NodeAddress;
            var peer = node == primary.NodeAddress ? standby.NodeAddress : primary.NodeAddress;
            var policy = new PostgreSqlFailbackSetup(node, peer, preferred.LiveNode.NodeAddress, primary.Local.SystemIdentifier!, enabled);
            _wizardPairVerified = false;
            WizardResult.Text = "После применения настроек обновите проверку пары.";
            UpdateWizardPolicyButton();
            await RunControllerActionAsync(ControllerAction.Configure, policy);
        }
        catch (Exception ex)
        {
            ActionStatus.Text = ex.Message;
        }
    }
}
