using ILD.Data.Enums;

namespace ILD.Data.Analytics;

/// <summary>Edge routing role used to attribute a node visit to on_failure / reject routings.</summary>
public sealed record EdgeInfo(EdgeType Type, string? Name);

/// <summary>The slice of a <c>LoopRunNode</c> the analytics rollup needs.</summary>
public sealed record NodeFact(
    DateTime? StartedAt,
    DateTime? CompletedAt,
    Guid? IncomingEdgeId,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    string? AiProvider);

/// <summary>A human-feedback request/response event used to compute turnaround.</summary>
public sealed record FeedbackFact(EventType EventType, int Sequence, DateTime Timestamp);

/// <summary>One run's contribution to a (day, template, provider) bucket.</summary>
public sealed record RunContribution(DateTime BucketDate, Guid TemplateId, string TemplateName, string AiProvider, AnalyticsMetrics Metrics);

/// <summary>
/// Additive metric accumulator shared by the live (read-time) and archived
/// (delete-time) analytics paths so a run is reduced to numbers in exactly one
/// place. Averages are derived from the totals + counts after merging.
/// </summary>
public sealed class AnalyticsMetrics
{
    public int RunCount { get; set; }
    public int CompletedRuns { get; set; }
    public int FailedRuns { get; set; }
    public int CancelledRuns { get; set; }
    public int NodeCount { get; set; }
    public double TotalNodeSeconds { get; set; }
    public int OnFailureRoutings { get; set; }
    public int RejectRoutings { get; set; }
    public int FeedbackCount { get; set; }
    public double TotalFeedbackSeconds { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }

    public void Add(AnalyticsMetrics o)
    {
        RunCount += o.RunCount;
        CompletedRuns += o.CompletedRuns;
        FailedRuns += o.FailedRuns;
        CancelledRuns += o.CancelledRuns;
        NodeCount += o.NodeCount;
        TotalNodeSeconds += o.TotalNodeSeconds;
        OnFailureRoutings += o.OnFailureRoutings;
        RejectRoutings += o.RejectRoutings;
        FeedbackCount += o.FeedbackCount;
        TotalFeedbackSeconds += o.TotalFeedbackSeconds;
        TotalInputTokens += o.TotalInputTokens;
        TotalOutputTokens += o.TotalOutputTokens;
        TotalCostUsd += o.TotalCostUsd;
    }

    /// <summary>Completed runs as a fraction of terminal runs; 0 when none are terminal.</summary>
    public double SuccessRate
    {
        get
        {
            var terminal = CompletedRuns + FailedRuns + CancelledRuns;
            return terminal > 0 ? (double)CompletedRuns / terminal : 0;
        }
    }

    public double? AvgNodeSeconds => NodeCount > 0 ? TotalNodeSeconds / NodeCount : null;
    public double? AvgFeedbackSeconds => FeedbackCount > 0 ? TotalFeedbackSeconds / FeedbackCount : null;
}

/// <summary>
/// Reduces a single run (its status, nodes, edge attribution, and feedback
/// events) to one <see cref="RunContribution"/>. Used by the analytics read
/// path for live runs and by the store's delete path to archive a run before
/// its rows are removed, so both sources are computed identically.
/// </summary>
public static class RunAnalyticsAggregator
{
    /// <summary>Bucket/provider label for a run that used no AI provider.</summary>
    public const string NoProvider = "(none)";

    public static RunContribution BuildContribution(
        Guid templateId,
        string templateName,
        LoopRunStatus status,
        DateTime startedAt,
        IReadOnlyList<NodeFact> nodes,
        IReadOnlyDictionary<Guid, EdgeInfo> edges,
        IReadOnlyList<FeedbackFact> feedback)
    {
        var m = new AnalyticsMetrics { RunCount = 1 };
        switch (status)
        {
            case LoopRunStatus.Completed: m.CompletedRuns = 1; break;
            case LoopRunStatus.Failed: m.FailedRuns = 1; break;
            case LoopRunStatus.Cancelled: m.CancelledRuns = 1; break;
        }

        foreach (var n in nodes)
        {
            if (n.StartedAt.HasValue && n.CompletedAt.HasValue)
            {
                m.NodeCount++;
                m.TotalNodeSeconds += (n.CompletedAt.Value - n.StartedAt.Value).TotalSeconds;
            }
            if (n.IncomingEdgeId is Guid edgeId && edges.TryGetValue(edgeId, out var edge))
            {
                if (edge.Type == EdgeType.OnFailure)
                    m.OnFailureRoutings++;
                else if (edge.Type == EdgeType.Custom && string.Equals(edge.Name, "Reject", StringComparison.OrdinalIgnoreCase))
                    m.RejectRoutings++;
            }
            m.TotalInputTokens += n.InputTokens;
            m.TotalOutputTokens += n.OutputTokens;
            m.TotalCostUsd += n.CostUsd;
        }

        foreach (var (requested, received) in PairFeedback(feedback))
        {
            m.FeedbackCount++;
            m.TotalFeedbackSeconds += (received - requested).TotalSeconds;
        }

        return new RunContribution(startedAt.Date, templateId, templateName, PrimaryProvider(nodes), m);
    }

    /// <summary>
    /// The provider that ran the most tokens for the run; the first node's
    /// provider on a tie, or <see cref="NoProvider"/> when no node carried one.
    /// A loop run is overwhelmingly single-provider (the template pins it), so
    /// attributing the whole run to one provider keeps the rollup coherent.
    /// </summary>
    private static string PrimaryProvider(IReadOnlyList<NodeFact> nodes)
    {
        string? best = null;
        long bestTokens = -1;
        foreach (var n in nodes)
        {
            if (string.IsNullOrEmpty(n.AiProvider)) continue;
            var tokens = n.InputTokens + n.OutputTokens;
            if (tokens > bestTokens)
            {
                bestTokens = tokens;
                best = n.AiProvider;
            }
        }
        return best ?? NoProvider;
    }

    /// <summary>Pair each human-feedback request with the next response, in sequence order.</summary>
    private static IEnumerable<(DateTime Requested, DateTime Received)> PairFeedback(IReadOnlyList<FeedbackFact> feedback)
    {
        DateTime? pending = null;
        foreach (var evt in feedback.OrderBy(f => f.Sequence))
        {
            if (evt.EventType == EventType.HumanFeedbackRequested)
                pending = evt.Timestamp;
            else if (evt.EventType == EventType.HumanFeedbackReceived && pending is DateTime requested)
            {
                yield return (requested, evt.Timestamp);
                pending = null;
            }
        }
    }
}
