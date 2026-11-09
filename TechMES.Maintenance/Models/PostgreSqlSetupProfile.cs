using System.IO;
using System.Text.RegularExpressions;
using TechMES.Contracts.Runtime;

namespace TechMES.Maintenance.Models;

/// <summary>Профиль компьютера БД, независимый от ролей серверов приложений.</summary>
public sealed class PostgreSqlSetupProfile
{
    public string Name { get; set; } = "Primary DB";
    public string ComputerName { get; set; } = "192.168.1.132";
    public string ServiceName { get; set; } = "postgresql-x64-18";
    public string BinDirectory { get; set; } = @"D:\PostgreSQL\18\bin";
    public string DataDirectory { get; set; } = @"D:\PostgreSQL\18\data";
    public int Port { get; set; } = 5432;

    /// <summary>Проверяет значения до экспорта или запуска локального инструмента.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Regex.IsMatch(ComputerName ?? "", @"^[A-Za-z0-9][A-Za-z0-9_.-]*$") || !Regex.IsMatch(ServiceName ?? "", @"^[A-Za-z0-9_.-]+$") || Port is < 1 or > 65535)
            throw new InvalidDataException("Проверьте имя компьютера PostgreSQL, службу и порт.");

        foreach (var path in new[] { BinDirectory, DataDirectory })
        {
            if (string.IsNullOrWhiteSpace(path) || !Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || path[3..].Split('\\', '/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl)))
                throw new InvalidDataException("Укажите локальные абсолютные пути PostgreSQL bin/data без завершающего слеша.");
        }
    }
}

/// <summary>Снимок проверки; дата обозначает момент проверки, а не текущее состояние удалённого сервера.</summary>
public sealed class PostgreSqlSetupReport
{
    public int Format { get; set; }
    public string ProfileFingerprint { get; set; } = "";
    public string Computer { get; set; } = "";
    public string[] Addresses { get; set; } = [];
    public DateTimeOffset CheckedAtUtc { get; set; }
    public bool Ready { get; set; }
    public string Message { get; set; } = "";
    public string LogPath { get; set; } = "";
    public PostgreSqlServiceState? State { get; set; }
}