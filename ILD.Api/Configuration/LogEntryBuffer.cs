using ILD.Data.DTOs.SignalRPayloads;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Api.Configuration;

/// <summary>
/// The tail of the process's own log, kept in memory for the Logging settings
/// page: a Serilog sink and the query behind it in one. Registered under the
/// same <see cref="LoggingLevelSwitch"/> as the console sink, so the level
/// control governs what the viewer can ever show. Nothing is written down —
/// what a restart loses is exactly what a restart loses from the level.
/// </summary>
public sealed class LogEntryBuffer : ILogEventSink
{
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly LogEntryPayload[] _ring;
    private int _next;
    private int _count;
    private long _lastId;

    public LogEntryBuffer(int capacity = DefaultCapacity)
    {
        Capacity = Math.Clamp(capacity, 1, 10_000);
        _ring = new LogEntryPayload[Capacity];
    }

    public int Capacity { get; }

    /// <summary>
    /// Announces an appended entry (in production: over SignalR). Assigned once
    /// the container exists — a sink is built before there is one.
    /// </summary>
    public Action<LogEntryPayload>? Appended { get; set; }

    public void Emit(LogEvent logEvent)
    {
        var rendered = Render(logEvent);
        LogEntryPayload entry;
        lock (_gate)
        {
            entry = rendered with { Id = ++_lastId };
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        if (Appended is not { } appended || CarriesAnAnnouncement(logEvent)) return;

        // Announcing must not itself log, and a failure must not surface as a
        // log event either: every event written from here is another entry to
        // announce.
        try { appended(entry); }
        catch { }
    }

    /// <summary>Newest first, oldest evicted, at most <paramref name="take"/> entries.</summary>
    public IReadOnlyList<LogEntryPayload> Entries(int take, LogEventLevel? minimumLevel = null, string? search = null)
    {
        if (take <= 0) return [];

        var needle = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var found = new List<LogEntryPayload>(Math.Min(take, Capacity));
        lock (_gate)
        {
            for (var back = 1; back <= _count && found.Count < take; back++)
            {
                var entry = _ring[(_next - back + Capacity) % Capacity];
                if (minimumLevel is { } floor
                    && Enum.TryParse<LogEventLevel>(entry.Level, out var level)
                    && level < floor)
                    continue;
                if (needle is not null && !Matches(entry, needle)) continue;
                found.Add(entry);
            }
        }
        return found;
    }

    private static bool Matches(LogEntryPayload entry, string needle)
        => entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An announcement travels over SignalR, and both the hub transport and the
    /// request log of a long-polling hub request write log events as it goes.
    /// Announcing those would announce the announcement: they are buffered like
    /// any other event and read back on the next request, but never pushed.
    /// </summary>
    private static bool CarriesAnAnnouncement(LogEvent logEvent)
    {
        var source = SourceContext(logEvent);
        return source.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal)
            || source.StartsWith("Microsoft.AspNetCore.Http.Connections", StringComparison.Ordinal)
            || (Scalar(logEvent, "RequestPath") is { } path
                && path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase));
    }

    private static LogEntryPayload Render(LogEvent logEvent) => new(
        Id: 0,
        Timestamp: logEvent.Timestamp,
        Level: logEvent.Level.ToString(),
        Source: SourceContext(logEvent),
        Message: logEvent.RenderMessage(),
        Detail: logEvent.Exception?.ToString());

    private static string SourceContext(LogEvent logEvent) => Scalar(logEvent, "SourceContext") ?? string.Empty;

    private static string? Scalar(LogEvent logEvent, string property)
        => logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: string text }
            ? text
            : null;
}
