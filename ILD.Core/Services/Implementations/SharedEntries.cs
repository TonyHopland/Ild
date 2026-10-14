namespace ILD.Core.Services.Implementations;

/// <summary>
/// Process-wide state kept per key only while someone uses it: <see cref="Enter"/>
/// hands out the key's entry, creating it for the first user, and the last
/// <see cref="Leave"/> removes and disposes it, so keys that come and go (chats,
/// repositories) leave nothing behind. Every <see cref="Enter"/> must be matched
/// by exactly one <see cref="Leave"/>, and the entry not touched after it.
/// </summary>
internal sealed class SharedEntries<TKey, TEntry>(Func<TEntry> create)
    where TKey : notnull
    where TEntry : IDisposable
{
    private readonly Dictionary<TKey, (TEntry Entry, int Users)> _entries = new();

    public TEntry Enter(TKey key)
    {
        lock (_entries)
        {
            var (entry, users) = _entries.TryGetValue(key, out var held) ? held : (create(), 0);
            _entries[key] = (entry, users + 1);
            return entry;
        }
    }

    public void Leave(TKey key)
    {
        TEntry entry;
        lock (_entries)
        {
            (entry, var users) = _entries[key];
            if (users > 1)
            {
                _entries[key] = (entry, users - 1);
                return;
            }
            _entries.Remove(key);
        }
        entry.Dispose();
    }

    /// <summary>Whether anyone uses the entry for <paramref name="key"/>.</summary>
    public bool Contains(TKey key)
    {
        lock (_entries)
            return _entries.ContainsKey(key);
    }
}
