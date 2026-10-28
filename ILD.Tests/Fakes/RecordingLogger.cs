using Microsoft.Extensions.Logging;

namespace ILD.Tests.Fakes;

internal sealed record LogEntry(LogLevel Level, string Text, Exception? Exception);

/// <summary>Keeps everything logged to it, from any thread, for a test to read back.</summary>
internal class RecordingLogger : ILogger
{
    private readonly List<LogEntry> _entries = new();
    private readonly Lock _gate = new();

    /// <summary>A snapshot, since the code under test may log from its own tasks.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public IReadOnlyList<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Text).ToList();

    public IReadOnlyList<string> Warnings => At(LogLevel.Warning);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>
{
}
