using ILD.Core.Services.Interfaces;
using ILD.Data.Analytics;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Computes the analytics dashboard's figures from the run data already
/// persisted. Two sources are merged: still-live runs (reduced from
/// <c>LoopRunNode</c>/event-log rows via <see cref="RunAnalyticsAggregator"/>)
/// and the durable <c>LoopRunAnalyticsBucket</c> archive of reclaimed runs. A run
/// is in exactly one source — live until deleted, archived after — so they never
/// double-count, and the dashboard keeps reporting history after cleanup.
/// Filtering (date range, provider) and rollup (template, provider, time series)
/// happen in memory over the small per-run contribution set, which keeps the
/// queries provider-agnostic (SQLite in tests, Postgres in production).
/// </summary>
public sealed class RunAnalyticsService : IRunAnalyticsService
{
    private readonly AppDbContext _db;

    public RunAnalyticsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RunAnalyticsOverview> GetOverviewAsync(AnalyticsQuery query, CancellationToken cancellationToken = default)
    {
        var contributions = new List<RunContribution>();
        contributions.AddRange(await BuildLiveContributionsAsync(cancellationToken));
        contributions.AddRange(await LoadArchivedContributionsAsync(cancellationToken));

        // Provider list for the filter control is the full (unfiltered) set so a
        // provider stays selectable even after its only runs fall outside the range.
        var availableProviders = contributions
            .Select(c => c.AiProvider)
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filtered = contributions.Where(c => Matches(c, query)).ToList();

        var templates = filtered
            .GroupBy(c => c.TemplateId)
            .Select(g =>
            {
                var metrics = Sum(g.Select(c => c.Metrics));
                var name = g.OrderByDescending(c => c.BucketDate).First().TemplateName;
                return new TemplateAnalytics(
                    g.Key, name,
                    metrics.RunCount, metrics.CompletedRuns, metrics.FailedRuns, metrics.CancelledRuns,
                    metrics.SuccessRate, metrics.AvgNodeSeconds,
                    metrics.OnFailureRoutings, metrics.RejectRoutings, metrics.AvgFeedbackSeconds,
                    metrics.TotalInputTokens, metrics.TotalOutputTokens, metrics.TotalCostUsd);
            })
            .OrderByDescending(t => t.TotalCostUsd)
            .ThenByDescending(t => t.TotalRuns)
            .ToList();

        var providers = filtered
            .GroupBy(c => c.AiProvider)
            .Select(g =>
            {
                var metrics = Sum(g.Select(c => c.Metrics));
                return new ProviderAnalytics(
                    g.Key, metrics.RunCount, metrics.TotalInputTokens, metrics.TotalOutputTokens, metrics.TotalCostUsd);
            })
            .OrderByDescending(p => p.TotalCostUsd)
            .ThenByDescending(p => p.TotalRuns)
            .ToList();

        var series = filtered
            .GroupBy(c => PeriodStart(c.BucketDate, query.Granularity))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var metrics = Sum(g.Select(c => c.Metrics));
                return new AnalyticsSeriesPoint(
                    DateOnly.FromDateTime(g.Key),
                    metrics.RunCount, metrics.TotalInputTokens, metrics.TotalOutputTokens, metrics.TotalCostUsd);
            })
            .ToList();

        var totals = Sum(filtered.Select(c => c.Metrics));

        return new RunAnalyticsOverview(
            totals.RunCount,
            totals.TotalInputTokens,
            totals.TotalOutputTokens,
            totals.TotalCostUsd,
            templates,
            providers,
            series,
            availableProviders,
            query.Granularity);
    }

    private async Task<List<RunContribution>> BuildLiveContributionsAsync(CancellationToken ct)
    {
        var runs = await _db.LoopRuns
            .Select(r => new
            {
                r.Id,
                TemplateId = r.LoopTemplateVersion.LoopTemplateId,
                TemplateName = r.LoopTemplateVersion.LoopTemplate.Name,
                r.Status,
                Started = r.StartedAt ?? r.CreatedAt,
            })
            .ToListAsync(ct);

        var nodes = await _db.LoopRunNodes
            .Select(n => new
            {
                n.LoopRunId,
                n.StartedAt,
                n.CompletedAt,
                n.IncomingEdgeId,
                n.InputTokens,
                n.OutputTokens,
                n.CostUsd,
                n.AiProvider,
            })
            .ToListAsync(ct);

        var edges = await _db.LoopNodeEdges
            .Select(e => new { e.Id, e.EdgeType, e.Name })
            .ToDictionaryAsync(e => e.Id, e => new EdgeInfo(e.EdgeType, e.Name), ct);

        var feedback = await _db.EventLogs
            .Where(e => e.LoopRunId != null
                && (e.EventType == EventType.HumanFeedbackRequested
                    || e.EventType == EventType.HumanFeedbackReceived))
            .Select(e => new { RunId = e.LoopRunId!.Value, e.EventType, e.Sequence, e.Timestamp })
            .ToListAsync(ct);

        var nodesByRun = nodes.ToLookup(n => n.LoopRunId);
        var feedbackByRun = feedback.ToLookup(f => f.RunId);

        var result = new List<RunContribution>(runs.Count);
        foreach (var run in runs)
        {
            var nodeFacts = nodesByRun[run.Id]
                .Select(n => new NodeFact(
                    n.StartedAt, n.CompletedAt, n.IncomingEdgeId,
                    n.InputTokens ?? 0, n.OutputTokens ?? 0, n.CostUsd ?? 0m, n.AiProvider))
                .ToList();
            var feedbackFacts = feedbackByRun[run.Id]
                .Select(f => new FeedbackFact(f.EventType, f.Sequence, f.Timestamp))
                .ToList();
            result.Add(RunAnalyticsAggregator.BuildContribution(
                run.TemplateId, run.TemplateName, run.Status, run.Started, nodeFacts, edges, feedbackFacts));
        }
        return result;
    }

    private async Task<List<RunContribution>> LoadArchivedContributionsAsync(CancellationToken ct)
    {
        var buckets = await _db.LoopRunAnalyticsBuckets.ToListAsync(ct);
        return buckets.Select(b => new RunContribution(
            b.BucketDate, b.LoopTemplateId, b.TemplateName, b.AiProvider,
            new AnalyticsMetrics
            {
                RunCount = b.RunCount,
                CompletedRuns = b.CompletedRuns,
                FailedRuns = b.FailedRuns,
                CancelledRuns = b.CancelledRuns,
                NodeCount = b.NodeCount,
                TotalNodeSeconds = b.TotalNodeSeconds,
                OnFailureRoutings = b.OnFailureRoutings,
                RejectRoutings = b.RejectRoutings,
                FeedbackCount = b.FeedbackCount,
                TotalFeedbackSeconds = b.TotalFeedbackSeconds,
                TotalInputTokens = b.TotalInputTokens,
                TotalOutputTokens = b.TotalOutputTokens,
                TotalCostUsd = b.TotalCostUsd,
            })).ToList();
    }

    private static bool Matches(RunContribution c, AnalyticsQuery query)
    {
        var day = DateOnly.FromDateTime(c.BucketDate);
        if (query.From is DateOnly from && day < from) return false;
        if (query.To is DateOnly to && day > to) return false;
        if (!string.IsNullOrEmpty(query.Provider)
            && !string.Equals(c.AiProvider, query.Provider, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static AnalyticsMetrics Sum(IEnumerable<AnalyticsMetrics> metrics)
    {
        var total = new AnalyticsMetrics();
        foreach (var m in metrics) total.Add(m);
        return total;
    }

    /// <summary>First UTC day of the bucket containing <paramref name="date"/> at the given granularity.</summary>
    private static DateTime PeriodStart(DateTime date, AnalyticsGranularity granularity) => granularity switch
    {
        AnalyticsGranularity.Week => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), // ISO week, Monday start
        AnalyticsGranularity.Month => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        AnalyticsGranularity.Year => new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => date.Date,
    };
}
