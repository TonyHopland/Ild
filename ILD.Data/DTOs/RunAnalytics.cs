using ILD.Data.Enums;

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
/// run start date, or the day a chat's usage was recorded (inclusive, UTC days);
/// null means unbounded. <see cref="Provider"/> restricts to one agent provider
/// (matched against the run's primary provider, or the provider of the chat's
/// call); null means all. <see cref="Granularity"/> controls the time-series bucket size.
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

/// <summary>
/// One template node's visits in the runs behind a <see cref="TemplateAnalyticsDetail"/>:
/// how often it ran, how often a visit ended <c>Failed</c>, and the mean seconds of
/// the visits that both started and completed (null when none did).
/// <see cref="TemplateVersion"/> is the version number the node belongs to; each
/// version stores its own nodes, so two versions' "Coder" are two rows.
/// </summary>
public sealed record TemplateNodeAnalytics(
    Guid NodeId,
    string Label,
    NodeType NodeType,
    int TemplateVersion,
    int Executions,
    int Failures,
    double? AvgSeconds);

/// <summary>
/// A template's dashboard row with per-node figures beside it. The figures in
/// <see cref="Totals"/> include deleted runs' archive buckets; <see cref="Nodes"/>
/// cannot, because a bucket keeps no per-node history, so they come from the
/// <see cref="RunsWithNodeDetail"/> still-existing runs only and list the nodes of
/// the versions those runs used.
/// </summary>
public sealed record TemplateAnalyticsDetail(
    TemplateAnalytics Totals,
    int RunsWithNodeDetail,
    IReadOnlyList<TemplateNodeAnalytics> Nodes);

/// <summary>
/// Per-agent-provider cost/token/run rollup, chats' usage included; <c>TotalRuns</c>
/// counts loop runs only. Provider is "(none)" for runs with no AI node.
/// </summary>
public sealed record ProviderAnalytics(
    string Provider,
    int TotalRuns,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd);

/// <summary>
/// What chats cost, one row for the chats a person started (<see cref="ScheduleId"/>
/// null) and one per Chat Schedule for the chats it started. Only usage the provider
/// reported is counted; title calls add to the figures but not to
/// <see cref="TotalTurns"/>. <see cref="ScheduleName"/> is the schedule's current
/// name, or the last one recorded once it is deleted.
/// </summary>
public sealed record ChatAnalytics(
    Guid? ScheduleId,
    string? ScheduleName,
    int TotalTurns,
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
/// filters) plus per-template, per-chat-row and per-provider breakdowns, a time
/// series at the requested granularity, and the full provider list for the filter
/// control. Chats add tokens and cost to the totals, providers and series, never
/// runs.
/// </summary>
public sealed record RunAnalyticsOverview(
    int TotalRuns,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd,
    IReadOnlyList<TemplateAnalytics> Templates,
    IReadOnlyList<ChatAnalytics> Chats,
    IReadOnlyList<ProviderAnalytics> Providers,
    IReadOnlyList<AnalyticsSeriesPoint> Series,
    IReadOnlyList<string> AvailableProviders,
    AnalyticsGranularity Granularity);
