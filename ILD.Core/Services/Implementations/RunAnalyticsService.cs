using System.Data;
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
/// templates. The stored rows are filtered (date range, provider, template) and
/// chat usage summed per day in the database. A live run's provider is derived
/// from its nodes, so live runs are filtered by provider, and every source rolled
/// up (template, provider, time series), in memory.
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
        var filter = StoredRowFilter.Of(query);
        // One snapshot for every read: a run or chat deleted between the live and the
        // archive reads would otherwise be counted in both.
        List<RunContribution> live;
        List<RunContribution> archived;
        List<ChatContribution> filteredChats;
        List<string> storedProviders;
        Dictionary<Guid, string> schedules;
        await using (var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
        {
            live = (await BuildLiveContributionsAsync(_db.LoopRuns, cancellationToken)).Select(r => r.Contribution).ToList();
            archived = await LoadArchivedContributionsAsync(filter, templateId: null, cancellationToken);
            filteredChats = await LoadChatContributionsAsync(filter, cancellationToken);
            storedProviders = await _db.LoopRunAnalyticsBuckets.Select(b => b.AiProvider)
                .Union(_db.ChatUsages.Select(u => u.AiProvider))
                .Union(_db.ChatAnalyticsBuckets.Select(b => b.AiProvider))
                .ToListAsync(cancellationToken);
            schedules = await _db.ChatSchedules
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
            await snapshot.CommitAsync(cancellationToken);
        }

        // Provider list for the filter control is the full (unfiltered) set so a
        // provider stays selectable even after its only runs fall outside the range.
        var availableProviders = live
            .Select(c => c.AiProvider)
            .Concat(storedProviders)
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filtered = live.Where(c => Matches(c.BucketDate, c.AiProvider, query)).Concat(archived).ToList();
        var spend = filtered
            .Select(c => new Spend(
                c.BucketDate, c.AiProvider, c.Metrics.RunCount,
                c.Metrics.TotalInputTokens, c.Metrics.TotalOutputTokens, c.Metrics.TotalCostUsd))
            .Concat(filteredChats.Select(c => new Spend(
                c.BucketDate, c.AiProvider, 0, c.InputTokens, c.OutputTokens, c.CostUsd)))
            .ToList();

        var templates = RollUpTemplates(filtered);

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

    public async Task<IReadOnlyList<TemplateAnalyticsDetail>> GetTemplateAnalyticsAsync(
        AnalyticsQuery query, Guid? templateId, CancellationToken cancellationToken = default)
    {
        var filter = StoredRowFilter.Of(query);
        var runs = _db.LoopRuns.Where(r =>
            (filter.Start == null || (r.StartedAt ?? r.CreatedAt) >= filter.Start)
            && (filter.End == null || (r.StartedAt ?? r.CreatedAt) < filter.End)
            && (templateId == null || r.LoopTemplateVersion.LoopTemplateId == templateId));

        List<LiveRun> liveRuns;
        List<RunContribution> archived;
        List<TemplateNode> templateNodes;
        List<NodeVisit> visits;
        // One snapshot, as in GetOverviewAsync: a run deleted between the reads would otherwise count twice.
        await using (var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
        {
            liveRuns = (await BuildLiveContributionsAsync(runs, cancellationToken))
                .Where(r => Matches(r.Contribution.BucketDate, r.Contribution.AiProvider, query))
                .ToList();
            archived = await LoadArchivedContributionsAsync(filter, templateId, cancellationToken);
            var runIds = liveRuns.Select(r => r.RunId).ToList();
            visits = await _db.LoopRunNodes
                .Where(n => runIds.Contains(n.LoopRunId))
                .Select(n => new NodeVisit(n.LoopNodeId, n.Status, n.StartedAt, n.CompletedAt))
                .ToListAsync(cancellationToken);
            var versionIds = liveRuns.Select(r => r.VersionId).Distinct().ToList();
            templateNodes = await _db.LoopNodes
                .Where(n => versionIds.Contains(n.LoopTemplateVersionId))
                .Select(n => new TemplateNode(
                    n.Id, n.LoopTemplateVersion.LoopTemplateId, n.Label, n.NodeType, n.LoopTemplateVersion.VersionNumber))
                .ToListAsync(cancellationToken);
            await snapshot.CommitAsync(cancellationToken);
        }

        var runsByTemplate = liveRuns.ToLookup(r => r.Contribution.TemplateId);
        var nodesByTemplate = templateNodes.ToLookup(n => n.TemplateId);
        var visitsByNode = visits.ToLookup(v => v.LoopNodeId);
        return RollUpTemplates(liveRuns.Select(r => r.Contribution).Concat(archived))
            .Select(totals =>
            {
                var nodes = nodesByTemplate[totals.TemplateId]
                    .OrderBy(n => n.VersionNumber)
                    .ThenBy(n => n.Label, StringComparer.Ordinal)
                    .ThenBy(n => n.Id)
                    .Select(n => NodeFigures(n, visitsByNode[n.Id].ToList()))
                    .ToList();
                return new TemplateAnalyticsDetail(totals, runsByTemplate[totals.TemplateId].Count(), nodes);
            })
            .ToList();
    }

    private static TemplateNodeAnalytics NodeFigures(TemplateNode node, IReadOnlyList<NodeVisit> visits)
    {
        var timed = visits
            .Where(v => v.StartedAt.HasValue && v.CompletedAt.HasValue)
            .Select(v => (v.CompletedAt!.Value - v.StartedAt!.Value).TotalSeconds)
            .ToList();
        return new TemplateNodeAnalytics(
            node.Id, node.Label, node.NodeType, node.VersionNumber,
            visits.Count,
            visits.Count(v => v.Status == LoopRunNodeStatus.Failed),
            timed.Count == 0 ? null : timed.Average());
    }

    /// <summary>
    /// The dashboard's template rows: one per template, named by its newest
    /// contribution, costliest first and then busiest.
    /// </summary>
    private static List<TemplateAnalytics> RollUpTemplates(IEnumerable<RunContribution> contributions)
        => contributions
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

    /// <summary>The contributions of <paramref name="runs"/>, reading the nodes and feedback of those runs only.</summary>
    private async Task<List<LiveRun>> BuildLiveContributionsAsync(IQueryable<LoopRun> runs, CancellationToken ct)
    {
        var runIds = runs.Select(r => r.Id);
        var runRows = await runs
            .Select(r => new
            {
                r.Id,
                r.LoopTemplateVersionId,
                TemplateId = r.LoopTemplateVersion.LoopTemplateId,
                TemplateName = r.LoopTemplateVersion.LoopTemplate.Name,
                r.Status,
                Started = r.StartedAt ?? r.CreatedAt,
            })
            .ToListAsync(ct);

        var nodes = await _db.LoopRunNodes
            .Where(n => runIds.Contains(n.LoopRunId))
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
                && runIds.Contains(e.LoopRunId.Value)
                && (e.EventType == EventType.HumanFeedbackRequested
                    || e.EventType == EventType.HumanFeedbackReceived))
            .Select(e => new { RunId = e.LoopRunId!.Value, e.EventType, e.Id, e.Timestamp })
            .ToListAsync(ct);

        var nodesByRun = nodes.ToLookup(n => n.LoopRunId);
        var feedbackByRun = feedback.ToLookup(f => f.RunId);

        var result = new List<LiveRun>(runRows.Count);
        foreach (var run in runRows)
        {
            var nodeFacts = nodesByRun[run.Id]
                .Select(n => new NodeFact(
                    n.StartedAt, n.CompletedAt, n.IncomingEdgeId,
                    n.InputTokens ?? 0, n.OutputTokens ?? 0, n.CostUsd ?? 0m, n.AiProvider))
                .ToList();
            var feedbackFacts = feedbackByRun[run.Id]
                .Select(f => new FeedbackFact(f.EventType, f.Id, f.Timestamp))
                .ToList();
            result.Add(new LiveRun(
                run.Id,
                run.LoopTemplateVersionId,
                RunAnalyticsAggregator.BuildContribution(
                    run.TemplateId, run.TemplateName, run.Status, run.Started, nodeFacts, edges, feedbackFacts)));
        }
        return result;
    }

    private async Task<List<RunContribution>> LoadArchivedContributionsAsync(
        StoredRowFilter filter, Guid? templateId, CancellationToken ct)
    {
        var buckets = await _db.LoopRunAnalyticsBuckets
            .Where(b => (filter.Start == null || b.BucketDate >= filter.Start)
                && (filter.End == null || b.BucketDate < filter.End)
                && (filter.Provider == null || b.AiProvider.ToLower() == filter.Provider)
                && (templateId == null || b.LoopTemplateId == templateId))
            .ToListAsync(ct);
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

    /// <summary>
    /// Chat spend in the query's range and provider: the usage of live chats summed
    /// per day as an archive bucket would hold it, then the deleted chats' buckets.
    /// </summary>
    private async Task<List<ChatContribution>> LoadChatContributionsAsync(StoredRowFilter filter, CancellationToken ct)
    {
        var live = await ChatUsageLedger.SumPerDay(_db.ChatUsages
                .Where(u => (filter.Start == null || u.CreatedAt >= filter.Start)
                    && (filter.End == null || u.CreatedAt < filter.End)
                    && (filter.Provider == null || u.AiProvider.ToLower() == filter.Provider)))
            .ToListAsync(ct);
        var archived = await _db.ChatAnalyticsBuckets
            .Where(b => (filter.Start == null || b.BucketDate >= filter.Start)
                && (filter.End == null || b.BucketDate < filter.End)
                && (filter.Provider == null || b.AiProvider.ToLower() == filter.Provider))
            .ToListAsync(ct);
        return live.Concat(archived).Select(b => new ChatContribution(
            b.BucketDate, b.LastRecordedAt,
            b.ScheduleId,
            b.ScheduleName,
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
    /// The query's filters as stored rows are matched in the database: the days as
    /// UTC instants with the end exclusive, null where the range is open, and the
    /// provider lower-cased. UTC, because Postgres refuses any other kind of
    /// DateTime for its timestamptz columns.
    /// </summary>
    private sealed record StoredRowFilter(DateTime? Start, DateTime? End, string? Provider)
    {
        public static StoredRowFilter Of(AnalyticsQuery query) => new(
            query.From?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            query.To is DateOnly to && to < DateOnly.MaxValue
                ? to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                : null,
            string.IsNullOrEmpty(query.Provider) ? null : query.Provider.ToLowerInvariant());
    }

    /// <summary>A still-existing run's contribution, with the run and the version it ran.</summary>
    private sealed record LiveRun(Guid RunId, Guid VersionId, RunContribution Contribution);

    private sealed record NodeVisit(Guid LoopNodeId, LoopRunNodeStatus Status, DateTime? StartedAt, DateTime? CompletedAt);

    private sealed record TemplateNode(Guid Id, Guid TemplateId, string Label, NodeType NodeType, int VersionNumber);

    /// <summary>
    /// What chats spent on one day for one (schedule, provider): the calls of chats
    /// that still exist, or a deleted chats' archive bucket. <see cref="RecordedAt"/>
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
