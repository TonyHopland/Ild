using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Computes the analytics dashboard's per-template figures by reading the run
/// data already persisted. Like <see cref="MetricsCollector"/>, it pulls the
/// rows it needs and aggregates in memory — the row counts are small (one set
/// per run) and this keeps the queries provider-agnostic (SQLite in tests,
/// Postgres in production) and free of decimal-<c>Sum</c> translation quirks.
/// </summary>
public sealed class RunAnalyticsService : IRunAnalyticsService
{
    private readonly AppDbContext _db;

    public RunAnalyticsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RunAnalyticsOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var templateNames = await _db.LoopTemplates
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var runs = await _db.LoopRuns
            .Select(r => new RunRow(
                r.Id,
                r.LoopTemplateVersion.LoopTemplateId,
                r.Status,
                r.StartedAt ?? r.CreatedAt))
            .ToListAsync(cancellationToken);

        var nodes = await _db.LoopRunNodes
            .Select(n => new NodeRow(
                n.LoopRunId,
                n.StartedAt,
                n.CompletedAt,
                n.IncomingEdgeId,
                n.InputTokens ?? 0,
                n.OutputTokens ?? 0,
                n.CostUsd ?? 0m))
            .ToListAsync(cancellationToken);

        var edges = await _db.LoopNodeEdges
            .Select(e => new { e.Id, e.EdgeType, e.Name })
            .ToDictionaryAsync(e => e.Id, e => (e.EdgeType, e.Name), cancellationToken);

        var feedbackEvents = await _db.EventLogs
            .Where(e => e.LoopRunId != null
                && (e.EventType == EventType.HumanFeedbackRequested
                    || e.EventType == EventType.HumanFeedbackReceived))
            .Select(e => new FeedbackRow(e.LoopRunId!.Value, e.EventType, e.Sequence, e.Timestamp))
            .ToListAsync(cancellationToken);

        var runTemplate = runs.ToDictionary(r => r.RunId, r => r.TemplateId);
        var nodesByRun = nodes.ToLookup(n => n.RunId);
        var feedbackByRun = feedbackEvents.ToLookup(f => f.RunId);

        var templates = new List<TemplateAnalytics>();
        foreach (var group in runs.GroupBy(r => r.TemplateId))
        {
            var templateRuns = group.ToList();
            var templateNodes = templateRuns.SelectMany(r => nodesByRun[r.RunId]).ToList();

            var completed = templateRuns.Count(r => r.Status == LoopRunStatus.Completed);
            var failed = templateRuns.Count(r => r.Status == LoopRunStatus.Failed);
            var cancelled = templateRuns.Count(r => r.Status == LoopRunStatus.Cancelled);
            var terminal = completed + failed + cancelled;

            var durations = templateNodes
                .Where(n => n.StartedAt.HasValue && n.CompletedAt.HasValue)
                .Select(n => (n.CompletedAt!.Value - n.StartedAt!.Value).TotalSeconds)
                .ToList();

            var onFailure = templateNodes.Count(n => EdgeIs(edges, n.IncomingEdgeId, EdgeType.OnFailure, null));
            var reject = templateNodes.Count(n => EdgeIs(edges, n.IncomingEdgeId, EdgeType.Custom, "Reject"));

            var turnaround = ComputeFeedbackTurnaround(templateRuns, feedbackByRun);

            templates.Add(new TemplateAnalytics(
                TemplateId: group.Key,
                TemplateName: templateNames.GetValueOrDefault(group.Key, "(deleted template)"),
                TotalRuns: templateRuns.Count,
                CompletedRuns: completed,
                FailedRuns: failed,
                CancelledRuns: cancelled,
                SuccessRate: terminal > 0 ? (double)completed / terminal : 0,
                AvgNodeSeconds: durations.Count > 0 ? durations.Average() : null,
                OnFailureRoutings: onFailure,
                RejectRoutings: reject,
                AvgHumanFeedbackSeconds: turnaround,
                TotalInputTokens: templateNodes.Sum(n => n.InputTokens),
                TotalOutputTokens: templateNodes.Sum(n => n.OutputTokens),
                TotalCostUsd: templateNodes.Sum(n => n.CostUsd)));
        }

        // Newest activity first so the most-used templates lead the dashboard.
        templates = templates
            .OrderByDescending(t => runs.Where(r => r.TemplateId == t.TemplateId).Max(r => r.StartedAt))
            .ToList();

        return new RunAnalyticsOverview(
            TotalRuns: runs.Count,
            TotalInputTokens: nodes.Sum(n => n.InputTokens),
            TotalOutputTokens: nodes.Sum(n => n.OutputTokens),
            TotalCostUsd: nodes.Sum(n => n.CostUsd),
            Templates: templates);
    }

    private static bool EdgeIs(
        IReadOnlyDictionary<Guid, (EdgeType Type, string? Name)> edges,
        Guid? edgeId,
        EdgeType type,
        string? name)
    {
        if (edgeId is not Guid id || !edges.TryGetValue(id, out var edge))
            return false;
        return edge.Type == type
            && string.Equals(edge.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mean seconds between each human-feedback request and the next response,
    /// paired per run by event sequence. Returns null when no pair exists.
    /// </summary>
    private static double? ComputeFeedbackTurnaround(
        IEnumerable<RunRow> runs,
        ILookup<Guid, FeedbackRow> feedbackByRun)
    {
        var deltas = new List<double>();
        foreach (var run in runs)
        {
            var ordered = feedbackByRun[run.RunId].OrderBy(f => f.Sequence).ToList();
            DateTime? pendingRequest = null;
            foreach (var evt in ordered)
            {
                if (evt.EventType == EventType.HumanFeedbackRequested)
                    pendingRequest = evt.Timestamp;
                else if (evt.EventType == EventType.HumanFeedbackReceived && pendingRequest is DateTime requested)
                {
                    deltas.Add((evt.Timestamp - requested).TotalSeconds);
                    pendingRequest = null;
                }
            }
        }
        return deltas.Count > 0 ? deltas.Average() : null;
    }

    private sealed record RunRow(Guid RunId, Guid TemplateId, LoopRunStatus Status, DateTime StartedAt);

    private sealed record NodeRow(
        Guid RunId,
        DateTime? StartedAt,
        DateTime? CompletedAt,
        Guid? IncomingEdgeId,
        long InputTokens,
        long OutputTokens,
        decimal CostUsd);

    private sealed record FeedbackRow(Guid RunId, EventType EventType, int Sequence, DateTime Timestamp);
}
