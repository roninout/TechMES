using System.IO;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Находит корневую папку репозитория TechMES.
/// WPF-приложение при запуске из Visual Studio находится в bin/Debug,
/// поэтому нельзя полагаться на текущую рабочую папку.
/// </summary>
public sealed class RepositoryLocator
{
    /// <summary>
    /// Ищет ближайшую родительскую папку, в которой лежит TechMES.sln.
    /// Если solution не найден, возвращает текущую директорию, чтобы UI мог хотя бы показать ошибку путей.
    /// </summary>
    public DirectoryInfo LocateRepositoryRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TechMES.sln")))
                    return directory;

                directory = directory.Parent;
            }
        }

        return new DirectoryInfo(Environment.CurrentDirectory);
    }
}
