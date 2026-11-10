using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TechMES.PostgreSql.Control;

/// <summary>Один и тот же системный каталог используется Runtime и отдельной службой.</summary>
public sealed class PostgreSqlControllerLease : IPostgreSqlControllerLease
{
    private readonly string _root;

    /// <summary>Использует общий для всех Windows-сеансов каталог ProgramData.</summary>
    public PostgreSqlControllerLease() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TechMES", "PostgreSqlNodes"))
    {
    }

    /// <summary>Позволяет проверять блокировку в изолированном каталоге тестов.</summary>
    public PostgreSqlControllerLease(string root)
    {
        _root = Path.GetFullPath(root);
    }

    /// <summary>Возвращает каталог одного Windows service name независимо от регистра имени.</summary>
    private string DirectoryPath(string serviceName)
    {
        if (!Regex.IsMatch(serviceName, "^[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("Invalid PostgreSQL service name.", nameof(serviceName));

        return Path.Combine(_root, serviceName.ToUpperInvariant());
    }

    /// <summary>Возвращает путь опубликованного снимка локальной службы.</summary>
    public string SnapshotPath(string serviceName)
    {
        return Path.Combine(DirectoryPath(serviceName), "status.json");
    }

    /// <summary>FileShare.None исключает второй хост и освобождается при завершении процесса.</summary>
    public IDisposable? TryAcquire(string serviceName)
    {
        var directory = DirectoryPath(serviceName);

        Directory.CreateDirectory(directory);

        try
        {
            return new FileStream(Path.Combine(directory, "controller.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
        {
            return null;
        }
    }
}

public sealed class WindowsPostgreSqlServicePresence : IPostgreSqlServicePresence
{
    /// <summary>Проверяет регистрацию службы без запуска PowerShell и изменения SCM.</summary>
    public bool IsInstalled(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (!Regex.IsMatch(serviceName, "^[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("Invalid Windows service name.", nameof(serviceName));

        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);

        return service is not null;
    }
}