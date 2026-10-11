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
/// Chats are merged the same way, from <c>ChatUsage</c> and the
/// <c>ChatAnalyticsBucket</c> archive of deleted chats: they add tokens and cost
/// to the totals, providers and series, and have rows of their own instead of
/// templates. Filtering (date range, provider) and rollup (template, provider,
/// time series) happen in memory over the small contribution sets, which keeps the
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
        var chatContributions = new List<ChatContribution>();
        chatContributions.AddRange(await BuildLiveChatContributionsAsync(cancellationToken));
        chatContributions.AddRange(await LoadArchivedChatContributionsAsync(cancellationToken));

        // Provider list for the filter control is the full (unfiltered) set so a
        // provider stays selectable even after its only runs fall outside the range.
        var availableProviders = contributions
            .Select(c => c.AiProvider)
            .Concat(chatContributions.Select(c => c.AiProvider))
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filtered = contributions.Where(c => Matches(c.BucketDate, c.AiProvider, query)).ToList();
        var filteredChats = chatContributions.Where(c => Matches(c.BucketDate, c.AiProvider, query)).ToList();
        var spend = filtered
            .Select(c => new Spend(
                c.BucketDate, c.AiProvider, c.Metrics.RunCount,
                c.Metrics.TotalInputTokens, c.Metrics.TotalOutputTokens, c.Metrics.TotalCostUsd))
            .Concat(filteredChats.Select(c => new Spend(
                c.BucketDate, c.AiProvider, 0, c.InputTokens, c.OutputTokens, c.CostUsd)))
            .ToList();

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

        var schedules = await _db.ChatSchedules
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var chats = filteredChats
            .GroupBy(c => c.ScheduleId)
            .Select(g => new ChatAnalytics(
                g.Key,
                g.Key is not Guid scheduleId ? null
                    : schedules.TryGetValue(scheduleId, out var current) ? current
                    : g.OrderByDescending(c => c.RecordedAt).First().ScheduleName,
                g.Sum(c => c.Turns),
                g.Sum(c => c.InputTokens),
                g.Sum(c => c.OutputTokens),
                g.Sum(c => c.CostUsd)))
            .OrderByDescending(c => c.TotalCostUsd)
            .ThenByDescending(c => c.TotalTurns)
            .ToList();

        var providers = spend
            .GroupBy(s => s.AiProvider)
            .Select(g => new ProviderAnalytics(
                g.Key, g.Sum(s => s.Runs), g.Sum(s => s.InputTokens), g.Sum(s => s.OutputTokens), g.Sum(s => s.CostUsd)))
            .OrderByDescending(p => p.TotalCostUsd)
            .ThenByDescending(p => p.TotalRuns)
            .ToList();

        var series = spend
            .GroupBy(s => PeriodStart(s.BucketDate, query.Granularity))
            .OrderBy(g => g.Key)
            .Select(g => new AnalyticsSeriesPoint(
                DateOnly.FromDateTime(g.Key),
                g.Sum(s => s.Runs), g.Sum(s => s.InputTokens), g.Sum(s => s.OutputTokens), g.Sum(s => s.CostUsd)))
            .ToList();

        return new RunAnalyticsOverview(
            spend.Sum(s => s.Runs),
            spend.Sum(s => s.InputTokens),
            spend.Sum(s => s.OutputTokens),
            spend.Sum(s => s.CostUsd),
            templates,
            chats,
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
            .Select(e => new { RunId = e.LoopRunId!.Value, e.EventType, e.Id, e.Timestamp })
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
                .Select(f => new FeedbackFact(f.EventType, f.Id, f.Timestamp))
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

    private async Task<List<ChatContribution>> BuildLiveChatContributionsAsync(CancellationToken ct)
    {
        var usages = await _db.ChatUsages
            .Select(u => new
            {
                u.CreatedAt,
                u.ScheduleId,
                u.ScheduleName,
                u.AiProvider,
                u.Kind,
                u.InputTokens,
                u.OutputTokens,
                u.CostUsd,
            })
            .ToListAsync(ct);
        return usages.Select(u => new ChatContribution(
            u.CreatedAt.Date, u.CreatedAt, u.ScheduleId, u.ScheduleName, u.AiProvider,
            u.Kind == ChatUsageKind.Turn ? 1 : 0, u.InputTokens, u.OutputTokens, u.CostUsd ?? 0m)).ToList();
    }

    private async Task<List<ChatContribution>> LoadArchivedChatContributionsAsync(CancellationToken ct)
    {
        var buckets = await _db.ChatAnalyticsBuckets.ToListAsync(ct);
        return buckets.Select(b => new ChatContribution(
            b.BucketDate, b.BucketDate,
            b.ScheduleId == Guid.Empty ? null : b.ScheduleId,
            b.ScheduleId == Guid.Empty ? null : b.ScheduleName,
            b.AiProvider, b.TurnCount, b.TotalInputTokens, b.TotalOutputTokens, b.TotalCostUsd)).ToList();
    }

    private static bool Matches(DateTime bucketDate, string aiProvider, AnalyticsQuery query)
    {
        var day = DateOnly.FromDateTime(bucketDate);
        if (query.From is DateOnly from && day < from) return false;
        if (query.To is DateOnly to && day > to) return false;
        if (!string.IsNullOrEmpty(query.Provider)
            && !string.Equals(aiProvider, query.Provider, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static AnalyticsMetrics Sum(IEnumerable<AnalyticsMetrics> metrics)
    {
        var total = new AnalyticsMetrics();
        foreach (var m in metrics) total.Add(m);
        return total;
    }

    /// <summary>
    /// What chats spent on one day for one (schedule, provider): a single call while
    /// its chat lives, or a deleted chats' archive bucket. <see cref="RecordedAt"/>
    /// orders the names recorded for a schedule.
    /// </summary>
    private sealed record ChatContribution(
        DateTime BucketDate, DateTime RecordedAt, Guid? ScheduleId, string? ScheduleName, string AiProvider,
        int Turns, long InputTokens, long OutputTokens, decimal CostUsd);

    /// <summary>Tokens and cost on one day for one provider, from a run or a chat; only runs carry a run count.</summary>
    private sealed record Spend(DateTime BucketDate, string AiProvider, int Runs, long InputTokens, long OutputTokens, decimal CostUsd);

    /// <summary>First UTC day of the bucket containing <paramref name="date"/> at the given granularity.</summary>
    private static DateTime PeriodStart(DateTime date, AnalyticsGranularity granularity) => granularity switch
    {
        AnalyticsGranularity.Week => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), // ISO week, Monday start
        AnalyticsGranularity.Month => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        AnalyticsGranularity.Year => new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => date.Date,
    };
}
