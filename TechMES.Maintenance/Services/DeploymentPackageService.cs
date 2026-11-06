using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechMES.Maintenance.Models;

namespace TechMES.Maintenance.Services;

/// <summary>Готовит общий ZIP и локальные скрипты; Send только копирует файлы.</summary>
public sealed class DeploymentPackageService
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public const string EmbeddedProfileMarker = "$script:EmbeddedProfileBase64 = ''";

    /// <summary>Возвращает SHA256 файла без загрузки всего архива в память.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Проверяет имена ZIP до подготовки: обход каталогов, дубликаты и ссылки запрещены.</summary>
    private static void ValidateEntries(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) || name.TrimEnd('/').Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) || !names.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Недопустимый путь в ZIP: " + name);
        }
    }

    /// <summary>Создаёт новый комплект, не изменяя исходный ZIP и не обращаясь к серверам.</summary>
    public PreparedDeployment Prepare(PackageDeploymentOptions options, string updaterPath)
    {
        options.Validate(true);
        using var source = ZipFile.OpenRead(options.ArchivePath);
        ValidateEntries(source);
        var entry = source.GetEntry("manifest.json") ?? throw new InvalidDataException("В ZIP нет manifest.json.");
        if (entry.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Слишком большой manifest.json.");
        using var bytes = new MemoryStream();
        using (var stream = entry.Open()) stream.CopyTo(bytes);
        var manifestHash = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, true);
        using var manifest = JsonDocument.Parse(reader.ReadToEnd());
        if (manifest.RootElement.GetProperty("Format").GetInt32() != 1 || manifest.RootElement.GetProperty("Runtime").GetString() != "win-x64" || !manifest.RootElement.GetProperty("SelfContained").GetBoolean())
            throw new InvalidDataException("Нужен автономный пакет win-x64. Создайте новый архив кнопкой «Создать архив».");
        var available = manifest.RootElement.GetProperty("Components").EnumerateArray().Select(part => part.GetProperty("Key").GetString()).ToHashSet();
        if (options.Profiles.SelectMany(profile => profile.Components).Any(key => !available.Contains(key)))
            throw new InvalidDataException("В архиве отсутствует выбранный компонент.");

        var engine = File.ReadAllText(updaterPath, Encoding.UTF8);
        if (engine.Split(EmbeddedProfileMarker, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Неподходящая версия Tools/Update-TechMESServer.ps1.");

        var directory = Path.Combine(options.OutputFolder, "TechMES_Deploy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, "TechMES_Deployment.zip");
        try
        {
            File.Copy(options.ArchivePath, archivePath);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            {
                archive.GetEntry("Update-TechMESServer.ps1")?.Delete();
                foreach (var profile in options.Profiles)
                {
                    var json = JsonSerializer.Serialize(new { profile.Name, profile.ComputerName, profile.InstallRoot, profile.RuntimeStatusUrl, Components = profile.Components, ArchiveName = Path.GetFileName(archivePath), ManifestSha256 = manifestHash }, JsonOptions);
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                    var script = engine.Replace(EmbeddedProfileMarker, "$script:EmbeddedProfileBase64 = '" + encoded + "'", StringComparison.Ordinal);
                    var name = "Deploy-" + profile.Name + ".ps1";
                    File.WriteAllText(Path.Combine(directory, name), script, new UTF8Encoding(true));
                    archive.GetEntry(name)?.Delete();
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(true));
                    writer.Write(script);
                }
            }
            var files = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, HashFile, StringComparer.OrdinalIgnoreCase);
            return new PreparedDeployment(directory, archivePath, JsonSerializer.Serialize(options, JsonOptions), files);
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    /// <summary>Проверяет, что профили и подготовленные файлы не изменились после подготовки.</summary>
    public static void ValidatePrepared(PackageDeploymentOptions options, PreparedDeployment prepared)
    {
        if (JsonSerializer.Serialize(options, JsonOptions) != prepared.OptionsSnapshot)
            throw new InvalidOperationException("Профиль изменён. Нажмите «Подготовить комплект» повторно.");
        foreach (var file in prepared.Hashes)
            if (!HashFile(Path.Combine(prepared.Directory, file.Key)).Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Изменён подготовленный файл: " + file.Key);
    }

    /// <summary>Копирует ZIP и один скрипт в новый подкаталог UNC с текущими правами Windows.</summary>
    public string Send(PackageDeploymentOptions options, PreparedDeployment prepared, ServerDeploymentProfile profile)
    {
        ValidatePrepared(options, prepared);
        if (string.IsNullOrWhiteSpace(profile.ShareRoot) || !profile.ShareRoot.StartsWith(@"\\", StringComparison.Ordinal) || profile.ShareRoot.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new InvalidDataException("Укажите сетевую папку, например \\\\192.168.1.132\\TechMESUpdates.");
        var target = Path.Combine(profile.ShareRoot, Path.GetFileName(prepared.Directory));
        Directory.CreateDirectory(target);
        foreach (var name in new[] { Path.GetFileName(prepared.ArchivePath), "Deploy-" + profile.Name + ".ps1" })
        {
            var destination = Path.Combine(target, name);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                File.Copy(Path.Combine(prepared.Directory, name), temporary);
                if (!HashFile(temporary).Equals(prepared.Hashes[name], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Не совпала контрольная сумма: " + name);
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return target;
    }
}

/// <summary>Неизменяемая привязка подготовленного комплекта к выбранным профилям.</summary>
public sealed record PreparedDeployment(string Directory, string ArchivePath, string OptionsSnapshot, Dictionary<string, string> Hashes);
