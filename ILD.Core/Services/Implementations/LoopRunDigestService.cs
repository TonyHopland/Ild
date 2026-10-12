using System.Data;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public sealed class LoopRunDigestService : ILoopRunDigestService
{
    private const int MaxTextLength = 2000;

    private static readonly EventType[] FeedbackEvents =
        [EventType.HumanFeedbackRequested, EventType.RunParked, EventType.HumanFeedbackReceived];

    private readonly AppDbContext _db;

    public LoopRunDigestService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<LoopRunDigest?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        // One snapshot: deleting a run removes its visits and events with it, so separate reads could pair its header with an empty path.
        await using var snapshot = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var run = await _db.LoopRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new
            {
                r.Id,
                r.WorkItemId,
                r.Status,
                r.IsPaused,
                r.IsHalted,
                r.HaltReason,
                r.HumanFeedbackReason,
                TemplateId = r.LoopTemplateVersion.LoopTemplateId,
                TemplateName = r.LoopTemplateVersion.LoopTemplate.Name,
                TemplateVersion = r.LoopTemplateVersion.VersionNumber,
                r.CreatedAt,
                r.StartedAt,
                r.CompletedAt,
                r.AiTraversalCount,
                r.ThrottleAutoResumeCount,
                r.NodeExecutionCount,
                r.ThrottleResetAt,
                r.CurrentNodeId,
                r.IncomingEdgeId,
                r.PrUrl,
                r.IsPrMerged,
                r.PrSnapshot,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null) return null;

        var visits = await _db.LoopRunNodes.AsNoTracking()
            .Where(n => n.LoopRunId == runId)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.StartedAt).ThenBy(n => n.Id)
            .Select(n => new
            {
                n.Id,
                n.LoopNodeId,
                Label = n.NodeLabel ?? n.LoopNode.Label,
                n.LoopNode.NodeType,
                n.Status,
                n.StartedAt,
                n.CompletedAt,
                n.Error,
                n.InputTokens,
                n.OutputTokens,
                n.CostUsd,
                n.AiProvider,
                n.PreviousNodeId,
                n.IncomingEdgeId,
            })
            .ToListAsync(cancellationToken);

        var edgeIds = visits.Select(v => v.IncomingEdgeId).Append(run.IncomingEdgeId)
            .OfType<Guid>().Distinct().ToList();
        var edges = await _db.LoopNodeEdges.AsNoTracking()
            .Where(e => edgeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EdgeType, e.Name, e.SourceNodeId, e.TargetNodeId, TargetLabel = e.TargetNode.Label })
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var feedback = await _db.EventLogs.AsNoTracking()
            .Where(e => e.LoopRunId == runId && FeedbackEvents.Contains(e.EventType))
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.EventType, e.Data, e.RunNodeId, e.NodeId, e.EdgeName, e.Timestamp })
            .ToListAsync(cancellationToken);
        await snapshot.CommitAsync(cancellationToken);

        var successors = visits.Where(v => v.PreviousNodeId.HasValue).ToLookup(v => v.PreviousNodeId!.Value);
        var lastVisitId = visits.Count == 0 ? (Guid?)null : visits[^1].Id;
        var nodes = visits.Select(v =>
        {
            var incoming = v.IncomingEdgeId is Guid inId && edges.TryGetValue(inId, out var inEdge)
                ? new LoopRunIncomingEdge(inEdge.Id, inEdge.EdgeType, inEdge.Name, inEdge.SourceNodeId)
                : null;
            var next = successors[v.Id].FirstOrDefault();
            // The edge the engine has followed but not yet entered is kept on the run, so it
            // is the last visit's edge when it leads from that node to where the run now is.
            var takenId = next is not null ? next.IncomingEdgeId
                : v.Id == lastVisitId ? run.IncomingEdgeId
                : null;
            var taken = takenId is Guid outId && edges.TryGetValue(outId, out var outEdge)
                    && outEdge.SourceNodeId == v.LoopNodeId
                    && (next is not null || outEdge.TargetNodeId == run.CurrentNodeId)
                ? new LoopRunEdgeTaken(outEdge.Id, outEdge.EdgeType, outEdge.Name, outEdge.TargetNodeId, outEdge.TargetLabel)
                : null;
            var (error, errorTruncated) = Bound(v.Error);
            return new LoopRunVisitDigest(
                v.Id, v.LoopNodeId, v.Label, v.NodeType, v.Status, v.StartedAt, v.CompletedAt,
                v.StartedAt.HasValue && v.CompletedAt.HasValue ? (v.CompletedAt.Value - v.StartedAt.Value).TotalSeconds : null,
                error, errorTruncated,
                v.InputTokens, v.OutputTokens, v.CostUsd, v.AiProvider,
                incoming, taken);
        }).ToList();

        var feedbackDigest = feedback.Select(e =>
        {
            var (text, textTruncated) = Bound(e.Data);
            return new LoopRunFeedbackDigest(
                e.Id, FeedbackKind(e.EventType), text, textTruncated, e.RunNodeId, e.NodeId, e.EdgeName, e.Timestamp);
        }).ToList();

        var (reason, reasonTruncated) = Bound(run.HumanFeedbackReason);
        return new LoopRunDigest(
            run.Id, run.WorkItemId, run.Status, run.IsPaused, run.IsHalted, run.HaltReason,
            reason, reasonTruncated,
            run.TemplateId, run.TemplateName, run.TemplateVersion,
            run.CreatedAt, run.StartedAt, run.CompletedAt,
            run.AiTraversalCount, run.ThrottleAutoResumeCount, run.NodeExecutionCount, run.ThrottleResetAt,
            nodes, feedbackDigest,
            run.PrUrl is null ? null : BuildPr(run.PrUrl, run.IsPrMerged, PrSnapshotJson.TryParse(run.PrSnapshot)));
    }

    private static LoopRunPrDigest BuildPr(string url, bool merged, RemotePrSnapshot? snapshot)
    {
        if (snapshot is null)
            return new LoopRunPrDigest(url, merged, null, null, null, null, null, []);

        var comments = (snapshot.Conversation ?? []).Select(c =>
        {
            var (body, bodyTruncated) = Bound(c.Body);
            return new LoopRunPrComment(c.Kind, c.Author, c.State, c.CreatedAt, body ?? string.Empty, bodyTruncated);
        }).ToList();
        return new LoopRunPrDigest(
            url, merged, snapshot.State, snapshot.Approved, snapshot.ChangesRequested, snapshot.Ci, snapshot.FetchedAt, comments);
    }

    private static string FeedbackKind(EventType type) => type switch
    {
        EventType.HumanFeedbackRequested => "requested",
        EventType.RunParked => "parked",
        EventType.HumanFeedbackReceived => "received",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>At most <see cref="MaxTextLength"/> characters of <paramref name="text"/>, never splitting a surrogate pair, and whether it was cut.</summary>
    private static (string? Text, bool Truncated) Bound(string? text)
    {
        if (text is null || text.Length <= MaxTextLength) return (text, false);
        var length = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return (text[..length], true);
    }
}
