namespace ILD.Data.DTOs;

/// <summary>
/// Token and cost accounting for a single AI turn, surfaced by an
/// <c>IAgentAdapter</c> from the agent CLI's own usage reporting. Persisted
/// per <c>LoopRunNode</c> and aggregated onto the run, work item, and loop
/// template for the analytics dashboard. <see cref="CostUsd"/> is null when the
/// CLI does not report a monetary cost (e.g. subscription-auth providers).
/// <see cref="AiProvider"/> is stamped by the AI node executor (the adapters
/// leave it null) so usage can be attributed to a provider on the dashboard.
/// <see cref="InputTokens"/> is the all-in input (fresh plus cache reads and writes);
/// <see cref="CacheReadTokens"/> and <see cref="CacheWriteTokens"/> are the cache parts of it
/// when the CLI reports them. <see cref="Turns"/> counts the turns the visit took (a session
/// woken again has more than one) and <see cref="Model"/> names the model(s) that spent the
/// tokens; each is null when the CLI does not report it.
/// </summary>
public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    string? AiProvider = null,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    int? Turns = null,
    string? Model = null)
{
    /// <summary>True when any field carries a non-zero figure worth persisting.</summary>
    public bool HasData => InputTokens > 0 || OutputTokens > 0 || (CostUsd ?? 0m) > 0m;
}
