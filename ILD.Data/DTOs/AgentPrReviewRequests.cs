using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for answering one review comment on its own thread
/// (<c>POST /api/v1/agent/workitems/{id}/pr-review/reply</c>). The comment id
/// comes from the review ledger, and only an id that work item's own pull
/// request holds is honoured.
/// </summary>
public class AgentPrReviewReplyRequest
{
    [StringLength(64)]
    public string CommentId { get; set; } = string.Empty;

    /// <summary>The answer. ILD stamps it before posting, so it never fires the comment edge back at the loop.</summary>
    [StringLength(65536)]
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// Request body for closing a review thread
/// (<c>POST /api/v1/agent/workitems/{id}/pr-review/resolve</c>). Without this an
/// item that was answered rather than changed resurfaces on every later review.
/// </summary>
public class AgentPrReviewResolveRequest
{
    [StringLength(256)]
    public string ThreadId { get; set; } = string.Empty;
}
