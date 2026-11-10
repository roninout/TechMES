using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TechMES.Contracts.Runtime;

namespace TechMES.PostgreSql.Control;

public sealed record PostgreSqlCommand(string Id, string Mode, string From, string To, string SystemIdentifier, DateTimeOffset CreatedAtUtc, JsonElement? Fence);

public sealed record PostgreSqlCommandJournal(string Id, string Fingerprint, string State, DateTimeOffset UpdatedAtUtc, string Message);

/// <summary>Защищённая локальная очередь; запросы и результаты не содержат паролей.</summary>
public sealed class PostgreSqlCommandStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; }
    public string CredentialPath => Path.Combine(Root, "credentials.bin");

    /// <summary>Оба вида хостов используют каталог одной и той же локальной службы.</summary>
    public PostgreSqlCommandStore(IOptions<PostgreSqlControlOptions> options, IPostgreSqlControllerLease leases)
    {
        Root = Path.Combine(Path.GetDirectoryName(leases.SnapshotPath(options.Value.ServiceName))!, "Commands");
    }

    /// <summary>Создаёт каталог с доступом только SYSTEM и локальным администраторам.</summary>
    public void Initialize()
    {
        AssertNoLinks(Root);

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Root);
            return; // Только изолированные тесты; настоящий runner требует Windows.
        }

        using var identity = WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("PostgreSQL commands require an administrative service account.");

        var security = new DirectorySecurity();

        security.SetAccessRuleProtection(true, false);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.SetOwner(admins);

        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        var directory = new DirectoryInfo(Root);

        if (directory.Exists)
            directory.SetAccessControl(security);
        else
            directory.Create(security);
    }

    /// <summary>Исключает junction/symlink во всём существующем пути.</summary>
    public static void AssertNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not allowed in the command store.");
        }
    }

    /// <summary>Читает небольшой JSON, исключая ссылки и неожиданно большие файлы.</summary>
    public static T Read<T>(string path)
    {
        AssertPrivate(path, false);

        if (new FileInfo(path).Length > 65536)
            throw new IOException("Command file is too large.");

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new IOException("Empty command file.");
    }

    /// <summary>Сбрасывает журнал на диск до атомарной замены, включая отметку до запуска процесса.</summary>
    public static void Write<T>(string path, T value)
    {
        AssertNoLinks(path);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new FileInfo(temporary).GetAccessControl();

                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

                new FileInfo(temporary).SetAccessControl(security);
            }

            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }

        File.Move(temporary, path, true);
    }

    /// <summary>Не позволяет читать зашифрованные пароли при ослабленном ACL файла.</summary>
    public void ValidateCredentials()
    {
        AssertPrivate(CredentialPath, false);

        if (!File.Exists(CredentialPath) || new FileInfo(CredentialPath).Length is < 1 or > 65536)
            throw new IOException("Configure local PostgreSQL command credentials first.");
    }

    /// <summary>Отвергает подложенные файлы и каталоги с посторонним владельцем или разрешениями.</summary>
    public static void AssertPrivate(string path, bool directory)
    {
        AssertNoLinks(path);

        if (!OperatingSystem.IsWindows())
            return;

        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();

        if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
            throw new UnauthorizedAccessException("The command store owner is not SYSTEM or Administrators.");

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("The command store ACL is not private.");
        }
    }
}