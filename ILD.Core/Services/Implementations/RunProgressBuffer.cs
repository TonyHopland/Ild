using System.Collections.Concurrent;
using System.Text;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// In-memory <see cref="IRunProgressBuffer"/>. Keeps one bounded character
/// buffer per active run. When a run's buffer grows past
/// <see cref="MaxCharsPerRun"/> the oldest characters are dropped (the live
/// view cares most about recent output, and the replay is necessarily lossy
/// for very long runs); the sequence counter keeps advancing so the
/// backlog→live handoff stays correct even across truncation.
/// </summary>
public sealed class RunProgressBuffer : IRunProgressBuffer
{
    /// <summary>Upper bound on retained characters per run (~512 KB).</summary>
    public const int MaxCharsPerRun = 512 * 1024;

    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly StringBuilder Text = new();
        public long Seq;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public long Append(Guid runId, string chunk)
    {
        chunk ??= string.Empty;
        var entry = _entries.GetOrAdd(runId, _ => new Entry());
        lock (entry.Gate)
        {
            entry.Text.Append(chunk);
            if (entry.Text.Length > MaxCharsPerRun)
                entry.Text.Remove(0, entry.Text.Length - MaxCharsPerRun);
            return ++entry.Seq;
        }
    }

    public RunProgressSnapshot Snapshot(Guid runId)
    {
        if (!_entries.TryGetValue(runId, out var entry))
            return new RunProgressSnapshot(string.Empty, 0);
        lock (entry.Gate)
        {
            return new RunProgressSnapshot(entry.Text.ToString(), entry.Seq);
        }
    }

    public void Clear(Guid runId) => _entries.TryRemove(runId, out _);
}
