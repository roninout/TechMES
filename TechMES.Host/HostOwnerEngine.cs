using TechMES.Contracts.Host;

namespace TechMES.Host;

public interface IHostVip
{
    HostNetworkSnapshot Read();
    Task SetAsync(bool assigned);
}

public interface IHostOwnerPeer
{
    Task<HostOwnerStatus> StatusAsync();
    Task ApplyAsync(HostApplyGrant command);
}

/// <summary>Плановая передача: durable revoke → отсутствие IP → durable grant. Автовыборов координатора нет.</summary>
public sealed partial class HostOwnerEngine(HostSharedAddressSettings settings, string pair, HostOwnerStore store, IHostVip vip, IHostOwnerPeer peer, Func<bool> webReady, HostWebFailoverSettings? webFailover = null)
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _node = new(1, 1);

    private volatile bool _stopping;

    private bool IsCoordinator => HostOwnerIdentity.Coordinator(settings) == settings.NodeAddress;

    /// <summary>Показывает журнал и сетевую готовность, не выполняя сетевых запросов к другому Host.</summary>
    public HostOwnerStatus Status()
    {
        var state = store.Current;
        var journal = state.Journal;

        bool Known(string address) => address == settings.NodeAddress || address == settings.PeerAddress;

        var invalidEmpty = journal.Phase == "Empty"
            && (journal.Epoch != 0 || journal.Owner != "" || journal.Target != "" || journal.Operation != Guid.Empty || journal.AutomaticWebFailover);

        var invalidOperation = journal.Phase != "Empty"
            && (journal.Epoch <= 0 || journal.Operation == Guid.Empty || !Known(journal.Target) || journal.Owner != "" && !Known(journal.Owner));

        var invalidOwner = journal.Phase is "Stable" or "Granting"
            && journal.Owner != journal.Target;

        if (state.Grant.Assigned && !state.Grant.Managed || invalidEmpty || invalidOperation || invalidOwner)
            throw new InvalidDataException("Несогласованный журнал владельца. Операции заблокированы.");

        var network = vip.Read();
        var present = network.Adapters.Any(adapter => adapter.Addresses.Any(address => address.Address == settings.VirtualAddress));

        // Существующий управляемый VIP не является ошибкой повторной проверки профиля.
        var checkNetwork = state.Grant.Managed
            ? network with
            {
                Adapters = network.Adapters.Select(adapter => adapter with
                {
                    Addresses = adapter.Addresses.Where(address => address.Address != settings.VirtualAddress).ToArray()
                }).ToArray()
            }
            : network;

        var check = HostNetworkPolicy.Check(settings, checkNetwork);

        var misplaced = network.Adapters.Any(adapter => adapter.Addresses.Any(address =>
            address.Address == settings.VirtualAddress
            && (Guid.Parse(adapter.Id) != Guid.Parse(settings.InterfaceId) || address.PrefixLength != settings.PrefixLength)));

        var errors = misplaced
            ? check.Errors.Append("Общий IP обнаружен на другом интерфейсе или с другим префиксом.").ToArray()
            : check.Errors;

        var addressReady = network.Adapters.SelectMany(adapter => adapter.Addresses)
            .Count(address => address.Address == settings.VirtualAddress && address.IsPreferred) == 1 && !misplaced;

        return new(pair, settings.NodeAddress, HostOwnerIdentity.Coordinator(settings), webReady(), errors.Length == 0, errors, present, addressReady, state.Grant, state.Journal, WebFailoverStatus);
    }

    /// <summary>Разрешает повторное сохранение неизменённой сети при уже управляемом VIP.</summary>
    public HostNetworkCheck CheckNetwork(HostSharedAddressSettings request)
    {
        var network = vip.Read();

        var unchanged = request.NodeAddress == settings.NodeAddress
            && request.PeerAddress == settings.PeerAddress
            && request.VirtualAddress == settings.VirtualAddress
            && request.PrefixLength == settings.PrefixLength
            && Guid.TryParse(request.InterfaceId, out var requested)
            && Guid.TryParse(settings.InterfaceId, out var current)
            && requested == current;

        if (!unchanged || !store.Current.Grant.Managed)
            return HostNetworkPolicy.Check(request, network);

        var status = Status();

        return new(request, network, status.Errors, ["Учитывается общий IP под управлением Host. Сетевой профиль не изменён."]);
    }

    /// <summary>Начинает операцию с проверкой версии; потерянный ответ не создаёт новую передачу.</summary>
    public async Task<HostOwnerStatus> ChangeAsync(HostOwnerRequest request, bool initialize, bool requireOwnerWebFailure = false)
    {
        if (!IsCoordinator || _stopping)
            throw new InvalidOperationException("Операцию принимает работающий координатор пары.");

        if (!await _operation.WaitAsync(0))
            throw new InvalidOperationException("Операция уже выполняется.");

        try
        {
            var journal = store.Current.Journal;

            if (request.Operation == Guid.Empty || request.Target != settings.NodeAddress && request.Target != settings.PeerAddress)
                throw new InvalidOperationException("Некорректная операция или адрес назначения.");

            if (journal.Operation == request.Operation)
            {
                if (journal.Target != request.Target)
                    throw new InvalidOperationException("OperationId уже использован с другой целью.");

                await ContinueAsync();

                return Status();
            }

            if (journal.Phase is "Releasing" or "Granting")
                throw new InvalidOperationException("Сначала продолжите незавершённую операцию.");

            if (request.ExpectedEpoch != journal.Epoch || initialize != (journal.Phase == "Empty"))
                throw new InvalidOperationException("Состояние изменилось. Обновите диагностику; первоначальное назначение доступно только пустой паре.");

            var remote = await peer.StatusAsync();

            ValidatePeer(remote);

            var local = Status();

            if (!local.NetworkReady || !remote.NetworkReady)
                throw new InvalidOperationException("Сетевая проверка не пройдена: " + string.Join("; ", local.Errors.Concat(remote.Errors)));

            var target = request.Target == settings.NodeAddress ? local : remote;

            if (!target.WebReady)
                throw new InvalidOperationException("WEB целевого узла не готов.");

            // Повторная проверка внутри блокировки операции: наблюдение могло устареть.
            if (requireOwnerWebFailure)
            {
                var previous = journal.Owner == settings.NodeAddress ? local : remote;

                if (initialize || journal.Phase != "Stable" || journal.Owner == request.Target || previous.WebReady || !previous.Grant.Managed || !previous.Grant.Assigned || previous.Grant.Epoch != journal.Epoch || target.Grant.Assigned || target.AddressPresent)
                    throw new InvalidOperationException("Условия автоматической передачи WEB изменились. Требуется новое наблюдение.");
            }

            if (initialize && (local.Grant.Epoch != 0 || remote.Grant.Epoch != 0 || local.Grant.Managed || remote.Grant.Managed || local.AddressPresent || remote.AddressPresent))
                throw new InvalidOperationException("Пара уже использовалась либо IP существует. Автоматическая повторная инициализация запрещена.");

            if (!initialize && journal.Owner == request.Target)
                return local;

            store.SaveJournal(new(checked(journal.Epoch + 1), journal.Owner, request.Target, "Releasing", request.Operation, requireOwnerWebFailure));

            await ContinueAsync();

            return Status();
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Продолжает только сохранённую операцию; не создаёт новый grant после неопределённого ответа.</summary>
    public async Task<HostOwnerStatus> ResumeAsync(Guid operation)
    {
        if (!IsCoordinator || _stopping || operation == Guid.Empty)
            throw new InvalidOperationException("Нужны координатор и сохранённая операция.");

        if (!await _operation.WaitAsync(0))
            throw new InvalidOperationException("Операция уже выполняется.");

        try
        {
            if (store.Current.Journal.Operation != operation)
                throw new InvalidOperationException("Операция уже изменилась.");

            await ContinueAsync();

            return Status();
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Никогда не выдаёт целевой grant до подтверждения удаления IP у прежнего владельца.</summary>
    private async Task ContinueAsync()
    {
        var journal = store.Current.Journal;

        if (journal.Phase == "Releasing")
        {
            if (journal.Owner.Length > 0)
                await ApplyToAsync(journal.Owner, new(pair, journal.Epoch, false));

            journal = journal with
            {
                Epoch = checked(journal.Epoch + 1),
                Owner = journal.Target,
                Phase = "Granting"
            };

            store.SaveJournal(journal);
        }

        if (journal.Phase == "Granting")
        {
            await ApplyToAsync(journal.Owner, new(pair, journal.Epoch, true));

            store.SaveJournal(journal with { Phase = "Stable" });
        }
    }

    /// <summary>Применяет разрешение последовательно; устаревшая команда не изменяет новый epoch.</summary>
    public async Task ApplyAsync(HostApplyGrant command)
    {
        await _node.WaitAsync();

        try
        {
            var before = store.Current.Grant;

            if (command.Pair != pair || command.Epoch <= 0 || command.Epoch < before.Epoch || command.Epoch == before.Epoch && command.Assigned != before.Assigned)
                throw new InvalidOperationException("Чужой, устаревший либо противоречивый grant.");

            var status = Status();

            if (command.Assigned)
            {
                if (_stopping)
                    throw new InvalidOperationException("Host останавливается.");

                if (!status.WebReady || !status.NetworkReady)
                    throw new InvalidOperationException("Целевой WEB или сеть не готовы.");
            }
            else if (!before.Managed && status.AddressPresent)
            {
                throw new InvalidOperationException("Host не будет удалять IP, которым он ранее не управлял.");
            }

            if (command.Assigned && command.Epoch == before.Epoch && status.AddressReady)
                return;

            store.SaveGrant(new(command.Epoch, command.Assigned, before.Managed || command.Assigned));

            await vip.SetAsync(command.Assigned);

            if (Status().AddressPresent != command.Assigned)
                throw new InvalidOperationException("Состояние IP не подтверждено.");
        }
        finally
        {
            _node.Release();
        }
    }

    /// <summary>Восстанавливает только действующий grant координатора; недоступность peer не разрешает захват IP.</summary>
    public async Task ReconcileAsync()
    {
        if (!store.Current.Grant.Managed || _stopping)
            return;

        var authority = IsCoordinator ? Status() : await peer.StatusAsync();

        if (!IsCoordinator)
            ValidatePeer(authority);

        var journal = authority.Journal;

        if (journal.Phase == "Empty" || journal.Epoch < store.Current.Grant.Epoch)
            throw new InvalidOperationException("Координатор не подтвердил журнал владельца.");

        var assigned = journal.Phase != "Releasing" && journal.Owner == settings.NodeAddress;

        if (assigned && !webReady())
            return;

        // Пассивный фоновый опрос не является подтверждением удаления для координатора.
        // RPC Apply(false) всегда заново выполняет проверку Windows.
        var local = store.Current.Grant;

        if (!assigned && local.Epoch == journal.Epoch && !local.Assigned && !Status().AddressPresent)
            return;

        await ApplyAsync(new(pair, journal.Epoch, assigned));
    }

    /// <summary>При штатной остановке снимает управляемый IP, сохраняя grant для следующего запуска.</summary>
    public async Task StopAsync()
    {
        _stopping = true;

        await _node.WaitAsync();

        try
        {
            if (store.Current.Grant.Managed)
                await vip.SetAsync(false);
        }
        finally
        {
            _node.Release();
        }
    }

    private Task ApplyToAsync(string node, HostApplyGrant command) =>
        node == settings.NodeAddress ? ApplyAsync(command) : peer.ApplyAsync(command);

    private void ValidatePeer(HostOwnerStatus remote)
    {
        if (remote.Pair != pair || remote.Node != settings.PeerAddress || remote.Coordinator != HostOwnerIdentity.Coordinator(settings))
            throw new InvalidOperationException("Профиль второго Host не совпадает с парой.");
    }
}