using System.IO;

namespace TechMES.Maintenance.Services;

/// <summary>
/// Находит исходники TechMES с учётом сохранённого выбора пользователя.
/// </summary>
public sealed class RepositoryLocator
{
    /// <summary>
    /// Хранит выбранный корень отдельно от каталогов публикации.
    /// </summary>
    private static string SelectionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechMES",
        "Maintenance",
        "repository.path");

    /// <summary>
    /// Запоминает существующий корень исходников TechMES.
    /// </summary>
    public void RememberRepositoryRoot(DirectoryInfo root)
    {
        if (!File.Exists(Path.Combine(root.FullName, "TechMES.sln")))
            throw new DirectoryNotFoundException("В выбранной папке нет TechMES.sln.");

        Directory.CreateDirectory(Path.GetDirectoryName(SelectionPath)!);
        File.WriteAllText(SelectionPath, root.FullName);
    }

    /// <summary>
    /// Сначала проверяет сохранённый выбор, затем родительские каталоги.
    /// Без исходников возвращает каталог самого приложения.
    /// </summary>
    public DirectoryInfo LocateRepositoryRoot()
    {
        try
        {
            if (File.Exists(SelectionPath))
            {
                var selected = File.ReadAllText(SelectionPath).Trim();

                if (!string.IsNullOrWhiteSpace(selected) &&
                    File.Exists(Path.Combine(selected, "TechMES.sln")))
                {
                    return new DirectoryInfo(selected);
                }
            }
        }
        catch (IOException)
        {
            // Недоступный сохранённый путь не блокирует запуск.
        }
        catch (UnauthorizedAccessException)
        {
            // Отсутствие доступа к выбору не блокирует диагностику.
        }

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

        return new DirectoryInfo(AppContext.BaseDirectory);
    }
}