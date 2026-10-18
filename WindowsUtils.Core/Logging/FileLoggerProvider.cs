using Microsoft.Extensions.Logging;

namespace WindowsUtils.Core.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes to a daily rolling text file, one per day
/// (<c>windowsutils-yyyyMMdd.log</c>). Thread-safe and lazy: the file is only opened on the
/// first write of each day. A failed write (disk full, missing AppData, ...) is swallowed so
/// logging can never take the application down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minLevel;

    /// <param name="directory">The folder that will contain the log files.</param>
    /// <param name="minLevel">Entries below this level are dropped.</param>
    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _writer = new FileLogWriter(directory);
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _minLevel);

    public void Dispose() => _writer.Dispose();
}

/// <summary>One logger per category, sharing the provider's single writer.</summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minLevel;

    public FileLogger(string category, FileLogWriter writer, LogLevel minLevel)
    {
        _category = category;
        _writer = writer;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var text = formatter(state, exception);
        if (exception is not null)
            text = text.Length == 0 ? exception.ToString() : text + Environment.NewLine + exception;

        _writer.Write(logLevel, _category, text);
    }
}

/// <summary>Appends formatted lines to the current day's file, rolling at midnight.</summary>
internal sealed class FileLogWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private string _currentPath = "";
    private bool _failed;

    public FileLogWriter(string directory) => _directory = directory;

    public void Write(LogLevel level, string category, string message)
    {
        if (_failed)
            return;

        var now = DateTime.Now;
        var path = Path.Combine(_directory, $"windowsutils-{now:yyyyMMdd}.log");
        try
        {
            lock (_lock)
            {
                if (_writer is null || _currentPath != path)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    _currentPath = path;
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{LevelLabel(level),-5}] [{category}] {message}");
            }
        }
        catch (Exception)
        {
            // Logging must never crash the app; stop trying once the file is unwritable.
            _failed = true;
            try { _writer?.Dispose(); } catch (Exception) { }
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();
    private NullScope() { }
    public void Dispose() { }
}
