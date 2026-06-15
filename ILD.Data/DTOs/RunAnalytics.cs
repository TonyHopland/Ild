namespace ILD.Data.DTOs;

/// <summary>
/// Per-loop-template rollup of the run data ILD already collects, so templates
/// become tunable with evidence rather than guessed-at. All figures are derived
/// from <c>LoopRun</c>, <c>LoopRunNode</c>, edge attribution, and the event log.
/// <list type="bullet">
///   <item><c>SuccessRate</c> — completed runs as a fraction of terminal
///   (completed+failed+cancelled) runs; 0 when none are terminal.</item>
///   <item><c>AvgNodeSeconds</c> — mean wall-clock seconds per executed node;
///   null when no node has finished.</item>
///   <item><c>OnFailureRoutings</c> / <c>RejectRoutings</c> — how often the
///   engine routed a node down its <c>on_failure</c> edge or a reject-pattern
///   (custom "Reject") edge.</item>
///   <item><c>AvgHumanFeedbackSeconds</c> — mean seconds between a
///   human-feedback request and the human's response; null when none recorded.</item>
/// </list>
/// </summary>
public sealed record TemplateAnalytics(
    Guid TemplateId,
    string TemplateName,
    int TotalRuns,
    int CompletedRuns,
    int FailedRuns,
    int CancelledRuns,
    double SuccessRate,
    double? AvgNodeSeconds,
    int OnFailureRoutings,
    int RejectRoutings,
    double? AvgHumanFeedbackSeconds,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd);

/// <summary>
/// The analytics dashboard payload: account-wide totals plus a per-template
/// breakdown, newest-activity first.
/// </summary>
public sealed record RunAnalyticsOverview(
    int TotalRuns,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd,
    IReadOnlyList<TemplateAnalytics> Templates);
