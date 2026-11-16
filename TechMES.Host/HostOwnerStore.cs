using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TechMES.Contracts.Host;

namespace TechMES.Host;

/// <summary>Append-only журнал с flush на диск и эксклюзивной блокировкой процесса. Повреждение блокирует запуск.</summary>
public sealed class HostOwnerStore : IDisposable
{
    public sealed record State(string Pair, string InterfaceId, HostOwnerJournal Journal, HostNodeGrant Grant);

    private sealed record Entry(string Payload, string Sha256);

    private readonly string _path;
    private readonly FileStream _lease;
    private readonly object _sync = new();

    private State _state;
    private volatile bool _faulted;

    public State Current =>
        !_faulted
            ? Volatile.Read(ref _state)
            : throw new IOException("Запись журнала Host не подтверждена; дальнейшие операции заблокированы.");

    public HostOwnerStore(string folder, string pair, string interfaceId, bool protect = true)
    {
        Directory.CreateDirectory(folder);

        if (OperatingSystem.IsWindows() && protect)
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);

            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                acl.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            new DirectoryInfo(folder).SetAccessControl(acl);
        }

        _path = Path.Combine(folder, "ownership.jsonl");
        _lease = new FileStream(Path.Combine(folder, "ownership.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);

                if (!text.EndsWith('\n'))
                    throw new InvalidDataException("Журнал Host оборван. Автоматический откат запрещён.");

                var entry = JsonSerializer.Deserialize<Entry>(text.TrimEnd('\n').Split('\n')[^1])
                    ?? throw new InvalidDataException("Пустой журнал Host.");

                if (Hash(entry.Payload) != entry.Sha256)
                    throw new InvalidDataException("Контрольная сумма журнала Host не совпадает.");

                _state = JsonSerializer.Deserialize<State>(entry.Payload)
                    ?? throw new InvalidDataException("Нет состояния Host.");
            }
            else
            {
                _state = new(pair, interfaceId, new(), new());
            }

            if (_state.Pair != pair
                || !Guid.TryParse(_state.InterfaceId, out var saved)
                || saved != Guid.Parse(interfaceId)
                || _state.Grant is null
                || _state.Journal is null
                || _state.Grant.Epoch < 0
                || _state.Journal.Epoch < 0
                || _state.Journal.Phase is not ("Empty" or "Stable" or "Releasing" or "Granting"))
            {
                throw new InvalidDataException("Журнал Host повреждён или принадлежит другому профилю.");
            }

            if (!File.Exists(_path))
                Save(state => state);
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
    }

    public void SaveJournal(HostOwnerJournal journal) =>
        Save(state => state with { Journal = journal });

    public void SaveGrant(HostNodeGrant grant)
    {
        if (Current.Grant != grant)
            Save(state => state with { Grant = grant });
    }

    public void Dispose() => _lease.Dispose();

    /// <summary>Не подтверждает изменение до Flush(true). Незавершённая последняя запись не игнорируется.</summary>
    private void Save(Func<State, State> update)
    {
        lock (_sync)
        {
            if (_faulted)
                throw new IOException("Журнал Host заблокирован после ошибки записи.");

            var next = update(_state);
            var payload = JsonSerializer.Serialize(next);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Entry(payload, Hash(payload))) + "\n");

            try
            {
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);

                stream.Write(bytes);
                stream.Flush(true);

                Volatile.Write(ref _state, next);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    private static string Hash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}