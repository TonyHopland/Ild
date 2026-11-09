using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The destinations the egress proxy refused a toolchain installer, per zone, while an install is running. An install
/// script fails with whatever its tool says about a refused connection (a reset, a TLS alert), which names no host; the
/// run host appends what is kept here so the operator sees which host to whitelist. Kept in memory, for the length of one
/// install only.
/// </summary>
public sealed class InstallerRefusals
{
    private const int MaxPerZone = 20;

    private readonly ConcurrentDictionary<string, List<string>> _refused = new(StringComparer.Ordinal);

    /// <summary>Starts a fresh list for <paramref name="zone"/>.</summary>
    public void Begin(string zone) => _refused[zone] = [];

    /// <summary>Notes a refused destination; only for a zone whose install has begun.</summary>
    public void Note(string zone, string host, int port)
    {
        if (!_refused.TryGetValue(zone, out var list)) return;
        var destination = $"{host}:{port}";
        lock (list)
            if (list.Count < MaxPerZone && !list.Contains(destination, StringComparer.Ordinal))
                list.Add(destination);
    }

    /// <summary>What was refused since <see cref="Begin"/>.</summary>
    public IReadOnlyList<string> Refused(string zone)
    {
        if (!_refused.TryGetValue(zone, out var list)) return [];
        lock (list) return [.. list];
    }

    /// <summary>Forgets the zone's list once its install has ended.</summary>
    public void End(string zone) => _refused.TryRemove(zone, out _);
}
