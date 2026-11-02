using Microsoft.Extensions.Logging;

namespace AiLocalPack.TestKit;

/// <summary>An <see cref="ILogger{T}"/> that records every entry so tests can assert what was logged.</summary>
public sealed class TestLogger<T> : ILogger<T>
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_lock)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
