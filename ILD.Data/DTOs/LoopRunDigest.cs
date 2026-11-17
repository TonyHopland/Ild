using ILD.Data.Enums;

namespace ILD.Data.DTOs;

/// <summary>
/// One loop run as a summary an agent can reason over: where it stands, the path it
/// took through the template, what it asked a human and what came back, and the
/// pull request it opened. Free texts are bounded and flagged when cut; node
/// output, effective input, transcripts, variables and session data are never part
/// of it.
/// </summary>
public sealed record LoopRunDigest(
    Guid Id,
    string WorkItemId,
    LoopRunStatus Status,
    bool IsPaused,
    bool IsHalted,
    HaltReason? HaltReason,
    string? HumanFeedbackReason,
    bool HumanFeedbackReasonTruncated,
    Guid TemplateId,
    string TemplateName,
    int TemplateVersion,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int AiTraversalCount,
    int ThrottleAutoResumeCount,
    int NodeExecutionCount,
    DateTime? ThrottleResetAt,
    IReadOnlyList<LoopRunVisitDigest> Nodes,
    IReadOnlyList<LoopRunFeedbackDigest> Feedback,
    LoopRunPrDigest? Pr);

/// <summary>
/// One node visit, in execution order. <see cref="IncomingEdge"/> is the edge
/// traversed to enter it; <see cref="EdgeTaken"/> is the edge the next visit
/// entered by, and only when that visit came from this one over an edge leaving
/// this node. For the last visit, when it is also the one that completed last and
/// no visit follows it yet, it is the edge the run has followed but not yet
/// entered, when that edge leaves this node for the run's current node.
/// Otherwise null.
/// </summary>
public sealed record LoopRunVisitDigest(
    Guid RunNodeId,
    Guid NodeId,
    string Label,
    NodeType NodeType,
    LoopRunNodeStatus Status,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    double? DurationSeconds,
    string? Error,
    bool ErrorTruncated,
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd,
    string? AiProvider,
    LoopRunIncomingEdge? IncomingEdge,
    LoopRunEdgeTaken? EdgeTaken);

public sealed record LoopRunIncomingEdge(Guid Id, EdgeType Type, string? Name, Guid SourceNodeId);

public sealed record LoopRunEdgeTaken(Guid Id, EdgeType Type, string? Name, Guid TargetNodeId, string TargetLabel);

/// <summary>
/// A feedback event: <c>requested</c> (a node asked a human), <c>parked</c> (the
/// run stopped to wait, for a human or for ILD itself, such as on an AI provider
/// throttle; <see cref="Text"/> gives the reason) or <c>received</c> (the human's
/// answer, with the edge it chose).
/// </summary>
public sealed record LoopRunFeedbackDigest(
    long EventId,
    string Kind,
    string? Text,
    bool TextTruncated,
    Guid? RunNodeId,
    Guid? NodeId,
    string? EdgeName,
    DateTime Timestamp);

/// <summary>
/// The run's pull request. The review fields come from the last stored PR
/// snapshot (as of <see cref="FetchedAt"/>) and are null, with no comments, when
/// none is stored.
/// </summary>
public sealed record LoopRunPrDigest(
    string Url,
    bool Merged,
    string? State,
    bool? Approved,
    bool? ChangesRequested,
    RemotePrCiStatus? Ci,
    DateTime? FetchedAt,
    IReadOnlyList<LoopRunPrComment> Comments);

public sealed record LoopRunPrComment(
    string Kind,
    string Author,
    string? State,
    DateTime CreatedAt,
    string Body,
    bool BodyTruncated);
