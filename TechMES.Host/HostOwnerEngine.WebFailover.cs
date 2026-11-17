using TechMES.Contracts.Host;

namespace TechMES.Host;

public sealed partial class HostOwnerEngine
{
    private readonly HostWebFailoverSettings _webFailover = webFailover ?? new();
    private readonly SemaphoreSlim _webFailoverCheck = new(1, 1);
    private HostWebFailoverStatus _webFailoverStatus = new(webFailover?.Enabled == true, "Starting", 0, 0, "Первое наблюдение ещё не выполнено.");
    private long _observedEpoch = -1;
    private int _ownerFailures;
    private int _targetSuccesses;

    public HostWebFailoverStatus WebFailoverStatus => Volatile.Read(ref _webFailoverStatus);

    /// <summary>Автоматика работает только у постоянного координатора и только при связи с обоими Host.</summary>
    public async Task CheckWebFailoverAsync()
    {
        if (!await _webFailoverCheck.WaitAsync(0)) return;

        try
        {
            _webFailover.Validate();

            if (!_webFailover.Enabled || _stopping || !IsCoordinator)
            {
                ResetWebFailover();
                PublishWebFailover(!_webFailover.Enabled ? "Disabled" : _stopping ? "Stopping" : "Follower", "Автоматическую передачу выполняет включённый координатор пары.");
                return;
            }

            var local = Status();
            var journal = local.Journal;

            if (journal.Phase is "Releasing" or "Granting")
            {
                ResetWebFailover();

                if (!journal.AutomaticWebFailover)
                {
                    PublishWebFailover("ManualOperation", "Ожидается завершение ручной операции.");
                    return;
                }

                // После рестарта продолжается сохранённая автоматическая операция с тем же OperationId.
                await ResumeAsync(journal.Operation);

                PublishWebFailover("Switched", "Сохранённая автоматическая передача завершена.");
                return;
            }

            if (journal.Phase != "Stable")
            {
                ResetWebFailover();
                PublishWebFailover("NotInitialized", "Сначала выполните первоначальное назначение IP.");
                return;
            }

            var remote = await peer.StatusAsync();

            ValidatePeer(remote);

            if (!local.NetworkReady || !remote.NetworkReady)
                throw new InvalidOperationException("Сетевая проверка одного из узлов не пройдена.");

            var previous = journal.Owner == settings.NodeAddress ? local : remote;
            var target = journal.Owner == settings.NodeAddress ? remote : local;

            if (!previous.Grant.Managed || !previous.Grant.Assigned || previous.Grant.Epoch != journal.Epoch || target.Grant.Assigned || target.AddressPresent)
                throw new InvalidOperationException("Grant узлов ещё не согласован с журналом владельца.");

            if (_observedEpoch != journal.Epoch)
            {
                ResetWebFailover();
                _observedEpoch = journal.Epoch;
            }

            _ownerFailures = previous.WebReady ? 0 : Math.Min(_ownerFailures + 1, _webFailover.FailureThreshold);
            _targetSuccesses = target.WebReady ? Math.Min(_targetSuccesses + 1, _webFailover.RecoveryThreshold) : 0;

            if (previous.WebReady)
            {
                PublishWebFailover("Monitoring", "WEB владельца готов. Обратная передача по предпочтению не выполняется.");
                return;
            }

            if (_ownerFailures < _webFailover.FailureThreshold || _targetSuccesses < _webFailover.RecoveryThreshold)
            {
                PublishWebFailover("Waiting", "Ожидается подтверждение отказа WEB владельца и готовности резерва.");
                return;
            }

            await ChangeAsync(new(target.Node, journal.Epoch, Guid.NewGuid()), false, requireOwnerWebFailure: true);

            ResetWebFailover();
            PublishWebFailover("Switched", "IP передан после подтверждённого удаления у прежнего владельца.");
        }
        catch (Exception ex)
        {
            ResetWebFailover();
            PublishWebFailover("Blocked", ex.Message);
        }
        finally
        {
            _webFailoverCheck.Release();
        }
    }

    /// <summary>Разрыв связи, смена epoch и ошибка обнуляют накопленные наблюдения.</summary>
    private void ResetWebFailover()
    {
        _observedEpoch = -1;
        _ownerFailures = 0;
        _targetSuccesses = 0;
    }

    private void PublishWebFailover(string state, string details) => Volatile.Write(ref _webFailoverStatus, new(_webFailover.Enabled, state, _ownerFailures, _targetSuccesses, details));
}