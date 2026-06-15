namespace ILD.Data.DTOs;

/// <summary>Time bucket size for the analytics series.</summary>
public enum AnalyticsGranularity
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>
/// Filters for an analytics query. <see cref="From"/>/<see cref="To"/> bound the
/// run start date (inclusive, UTC days); null means unbounded. <see cref="Provider"/>
/// restricts to one agent provider (matched against the run's primary provider);
/// null means all. <see cref="Granularity"/> controls the time-series bucket size.
/// </summary>
public sealed record AnalyticsQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Provider = null,
    AnalyticsGranularity Granularity = AnalyticsGranularity.Day);

/// <summary>
/// Per-loop-template rollup of the run data ILD already collects, so templates
/// become tunable with evidence rather than guessed-at. All figures are derived
/// from <c>LoopRun</c>, <c>LoopRunNode</c>, edge attribution, the event log, and
/// the durable <c>LoopRunAnalyticsBucket</c> archive of deleted runs.
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

/// <summary>Per-agent-provider cost/token/run rollup. Provider is "(none)" for runs with no AI node.</summary>
public sealed record ProviderAnalytics(
    string Provider,
    int TotalRuns,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd);

/// <summary>One point in the cost/token/run time series. <see cref="PeriodStart"/> is the bucket's first UTC day.</summary>
public sealed record AnalyticsSeriesPoint(
    DateOnly PeriodStart,
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

/// <summary>
/// The analytics dashboard payload: account-wide totals (within the active
/// filters) plus per-template and per-provider breakdowns, a time series at the
/// requested granularity, and the full provider list for the filter control.
/// </summary>
public sealed record RunAnalyticsOverview(
    int TotalRuns,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd,
    IReadOnlyList<TemplateAnalytics> Templates,
    IReadOnlyList<ProviderAnalytics> Providers,
    IReadOnlyList<AnalyticsSeriesPoint> Series,
    IReadOnlyList<string> AvailableProviders,
    AnalyticsGranularity Granularity);
