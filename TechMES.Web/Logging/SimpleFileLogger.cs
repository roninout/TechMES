using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TechMES.Web.Logging;

/// <summary>
/// Минимальный файловый logger для WEB-сервиса.
/// Он нужен обслуживающему приложению TechMES.Maintenance: Windows Service не показывает консоль,
/// а простой файл рядом с exe позволяет быстро понять, почему WEB не стартует или не отвечает.
/// </summary>
internal sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, SimpleFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SimpleFileLoggerOptions _options;
    private readonly object _writeLock = new();

    /// <summary>
    /// Создает provider и гарантирует наличие папки logs.
    /// </summary>
    public SimpleFileLoggerProvider(IOptions<SimpleFileLoggerOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(_options.GetFullDirectory());
    }

    /// <summary>
    /// Создает logger для категории ASP.NET Core или компонента TechMES.Web.
    /// </summary>
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SimpleFileLogger(name, _options, _writeLock));

    /// <summary>
    /// Очищает кеш logger-ов.
    /// </summary>
    public void Dispose() =>
        _loggers.Clear();
}

/// <summary>
/// Пишет лог-записи в дневной файл.
/// Файл открывается на время одной записи, поэтому сервис можно обновлять без удержания долгого handle-а.
/// </summary>
internal sealed class SimpleFileLogger(
    string categoryName,
    SimpleFileLoggerOptions options,
    object writeLock) : ILogger
{
    /// <summary>
    /// Scope в этом простом provider-е не используется.
    /// </summary>
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull =>
        NullScope.Instance;

    /// <summary>
    /// Проверяет, проходит ли запись по минимальному уровню.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) =>
        options.Enabled && logLevel >= options.MinimumLevel && logLevel != LogLevel.None;

    /// <summary>
    /// Формирует человекочитаемую строку и добавляет ее в logs/web-YYYYMMDD.log.
    /// </summary>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
            return;

        var now = DateTimeOffset.Now;
        var filePath = Path.Combine(
            options.GetFullDirectory(),
            $"{options.FileNamePrefix}-{now:yyyyMMdd}.log");

        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff zzz}] {logLevel,-11} {categoryName}: {message}";

        if (exception is not null)
            line += Environment.NewLine + exception;

        lock (writeLock)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
    }
}

/// <summary>
/// Настройки файлового logger-а WEB.
/// Directory можно оставить относительным, чтобы файл лежал рядом с опубликованным TechMES.Web.exe.
/// </summary>
internal sealed class SimpleFileLoggerOptions
{
    /// <summary>
    /// Включает файловое логирование.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Минимальный уровень записи.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Папка логов.
    /// </summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// Префикс имени файла.
    /// </summary>
    public string FileNamePrefix { get; set; } = "techmes-web";

    /// <summary>
    /// Возвращает абсолютный путь к папке логов.
    /// </summary>
    public string GetFullDirectory() =>
        Path.IsPathRooted(Directory)
            ? Directory
            : Path.Combine(AppContext.BaseDirectory, Directory);
}

/// <summary>
/// Пустой IDisposable для BeginScope.
/// </summary>
internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();

    public void Dispose()
    {
    }
}

/// <summary>
/// Extension-метод подключения простого файлового logger-а.
/// </summary>
internal static class SimpleFileLoggerExtensions
{
    public static ILoggingBuilder AddSimpleFile(
        this ILoggingBuilder builder,
        IConfiguration configuration)
    {
        builder.Services.Configure<SimpleFileLoggerOptions>(configuration.GetSection("FileLogging"));
        builder.Services.AddSingleton<ILoggerProvider, SimpleFileLoggerProvider>();
        return builder;
    }
}
