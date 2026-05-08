namespace ILD.WorkItemServer.Auth;

public sealed class ApiKeyOptions
{
    /// <summary>
    /// Comma- or whitespace-separated list of permitted API keys. Read from
    /// the WORKITEM_API_KEYS env var or the WorkItemServer:ApiKeys config
    /// value. Empty means no clients can authenticate (server refuses every
    /// request) — callers must configure at least one key.
    /// </summary>
    public string Keys { get; set; } = string.Empty;

    public IReadOnlySet<string> ParseKeys()
    {
        return Keys
            .Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}
