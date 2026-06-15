namespace ILD.Data.DTOs;

/// <summary>
/// Token and cost accounting for a single AI turn, surfaced by an
/// <c>IAgentAdapter</c> from the agent CLI's own usage reporting. Persisted
/// per <c>LoopRunNode</c> and aggregated onto the run, work item, and loop
/// template for the analytics dashboard. <see cref="CostUsd"/> is null when the
/// CLI does not report a monetary cost (e.g. subscription-auth providers).
/// <see cref="AiProvider"/> is stamped by the AI node executor (the adapters
/// leave it null) so usage can be attributed to a provider on the dashboard.
/// </summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens, decimal? CostUsd, string? AiProvider = null)
{
    /// <summary>True when any field carries a non-zero figure worth persisting.</summary>
    public bool HasData => InputTokens > 0 || OutputTokens > 0 || (CostUsd ?? 0m) > 0m;
}
