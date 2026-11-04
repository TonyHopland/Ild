namespace ILD.Core.Services.Implementations;

/// <summary>
/// The version each managed agent will be installed automatically once its cooldown has passed,
/// and from when, as the last registry check found it. Lets the agent list say "will install on
/// 3 June" without reading the registry's full release history on every request. In memory:
/// empty after a restart until the next check.
/// </summary>
public sealed class AutoInstallForecast
{
    private readonly Dictionary<string, (string Version, DateTimeOffset After)> _forecast = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Set(string agentKey, string version, DateTimeOffset after)
    {
        lock (_lock) _forecast[agentKey] = (version, after);
    }

    public void Clear(string agentKey)
    {
        lock (_lock) _forecast.Remove(agentKey);
    }

    public (string Version, DateTimeOffset After)? Get(string agentKey)
    {
        lock (_lock) return _forecast.TryGetValue(agentKey, out var value) ? value : null;
    }
}
