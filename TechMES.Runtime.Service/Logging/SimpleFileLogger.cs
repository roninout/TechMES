using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TechMES.Runtime.Service.Logging;

/// <summary>
/// Минимальный файловый logger для серверного режима Runtime.Service.
/// Не тянем внешний logging-пакет: Maintenance нужен простой .log-файл рядом с опубликованным exe,
/// чтобы на промышленном сервере можно было быстро посмотреть последние ошибки запуска и CtApi/PostgreSQL.
/// </summary>
internal sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, SimpleFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SimpleFileLoggerOptions _options;
    private readonly object _writeLock = new();

    /// <summary>
    /// Создает provider с уже прочитанными настройками FileLogging из appsettings.json.
    /// </summary>
    public SimpleFileLoggerProvider(IOptions<SimpleFileLoggerOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(_options.GetFullDirectory());
    }

    /// <summary>
    /// Возвращает logger для конкретной категории Microsoft.Extensions.Logging.
    /// </summary>
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new SimpleFileLogger(name, _options, _writeLock));

    /// <summary>
    /// Освобождает provider. Файлы открываются на короткое время при записи, поэтому закрывать поток не нужно.
    /// </summary>
    public void Dispose() =>
        _loggers.Clear();
}

/// <summary>
/// Пишет одну строку лога в дневной файл.
/// Такой подход проще, чем постоянно держать файл открытым, и хорошо подходит для сервисной диагностики.
/// </summary>
internal sealed class SimpleFileLogger(
    string categoryName,
    SimpleFileLoggerOptions options,
    object writeLock) : ILogger
{
    /// <summary>
    /// Scope не используется, но интерфейс ILogger требует вернуть IDisposable.
    /// </summary>
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull =>
        NullScope.Instance;

    /// <summary>
    /// Проверяет, нужно ли писать запись указанного уровня.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) =>
        options.Enabled && logLevel >= options.MinimumLevel && logLevel != LogLevel.None;

    /// <summary>
    /// Формирует строку и добавляет ее в файл logs/runtime-YYYYMMDD.log.
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
/// Настройки простого файлового logger-а.
/// Directory может быть относительным: тогда он считается от AppContext.BaseDirectory опубликованного сервиса.
/// </summary>
internal sealed class SimpleFileLoggerOptions
{
    /// <summary>
    /// Включает запись в файл.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Минимальный уровень, который попадет в файл.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Папка логов. По умолчанию logs рядом с exe.
    /// </summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// Префикс имени дневного файла.
    /// </summary>
    public string FileNamePrefix { get; set; } = "techmes-runtime";

    /// <summary>
    /// Возвращает абсолютный путь к папке логов.
    /// </summary>
    public string GetFullDirectory() =>
        Path.IsPathRooted(Directory)
            ? Directory
            : Path.Combine(AppContext.BaseDirectory, Directory);
}

/// <summary>
/// Пустой scope для ILogger.BeginScope.
/// </summary>
internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();

    public void Dispose()
    {
    }
}

/// <summary>
/// Extension-метод, чтобы подключение файлового logger-а в Program.cs оставалось одной строкой.
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
