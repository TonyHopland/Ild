using ILD.WorkItemServer.Domain;

namespace ILD.WorkItemServer.Dtos;

public sealed class WorkItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public WorkItemPriority Priority { get; set; }
    public WorkItemStatus Status { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ConversationMessage> Conversation { get; set; } = Array.Empty<ConversationMessage>();

    /// <summary>Every PR opened against this item, newest first.</summary>
    public IReadOnlyList<WorkItemPullRequest> PullRequests { get; set; } = Array.Empty<WorkItemPullRequest>();

    /// <summary>
    /// The item's attachments, metadata only — never the bytes, which are served
    /// one file at a time from the attachment routes. Empty on the poll
    /// response, which stays bodiless.
    /// </summary>
    public IReadOnlyList<WorkItemAttachmentDto> Attachments { get; set; } = Array.Empty<WorkItemAttachmentDto>();
    public string? HumanFeedbackActions { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? RepositoryId { get; set; }
    public AiProviderOverrideMode AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }

    /// <summary>The branch every run of this item uses, verbatim. Null = generated per-run name.</summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>The ref every run of this item branches from. Null = the repository's default branch.</summary>
    public string? BaseBranchOverride { get; set; }

    /// <summary>
    /// How many edit proposals on this item still wait for a human. Zero on the
    /// poll response, which stays bodiless.
    /// </summary>
    public int PendingEditProposalCount { get; set; }
}

public sealed class WorkItemAttachmentDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CreateWorkItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;
    public IReadOnlyList<string>? Tags { get; set; }
    public IReadOnlyList<string>? Dependencies { get; set; }
    public WorkItemStatus? ForceStatus { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? RepositoryId { get; set; }

    /// <summary>
    /// Custom branch name for every run of the new item. Blank/null leaves the
    /// item on the generated per-run naming.
    /// </summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>
    /// Ref the new item's runs branch from. Blank/null leaves them branching
    /// from the repository's default branch.
    /// </summary>
    public string? BaseBranchOverride { get; set; }
}

public sealed class UpdateWorkItemRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>
    /// Replaces the item's custom branch name. Null leaves it untouched; an
    /// empty/blank string clears it back to generated per-run naming — the same
    /// null-means-unchanged convention <see cref="Title"/> follows. Only the
    /// item's <em>next</em> run sees the change: a run already started keeps the
    /// branch it was created with, and nothing renames an existing branch or
    /// worktree.
    /// </summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>
    /// Replaces the ref the item's runs branch from, under the same
    /// null-means-unchanged / blank-means-clear convention as
    /// <see cref="BranchNameOverride"/>, and likewise only from the item's next
    /// run: a run already under way keeps the base it was created with.
    /// </summary>
    public string? BaseBranchOverride { get; set; }

    /// <summary>
    /// When supplied, replaces the work item's AI provider override. The mode
    /// and target id travel as a unit — supplying the mode also authoritatively
    /// sets <see cref="AiProviderOverrideId"/> (null clears the target).
    /// </summary>
    public AiProviderOverrideMode? AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }
}

public sealed class TransitionRequest
{
    public WorkItemStatus TargetStatus { get; set; }
    public string? Reason { get; set; }
    public string? Actions { get; set; }
    /// <summary>Optional author display name for the conversation entry this
    /// transition appends (e.g. the originating node's title).</summary>
    public string? Name { get; set; }
    /// <summary>The ILD node execution the appended conversation entry comes from.</summary>
    public Guid? RunNodeId { get; set; }
}

public sealed class TransitionResponse
{
    public bool Success { get; set; }
    public WorkItemStatus ActualStatus { get; set; }
    public string? Reason { get; set; }
}

public sealed class FeedbackRequest
{
    public string? Content { get; set; }
}

public sealed class AppendConversationRequest
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? Name { get; set; }
    public Guid? RunNodeId { get; set; }
}

/// <summary>
/// What came of recording a PR against a work item. Distinguished rather than
/// collapsed into a bool because they mean different things to a caller: only
/// <see cref="NotFound"/> says the request will never succeed, while
/// <see cref="Conflict"/> is worth retrying and must not be reported to a
/// client as "no such work item".
/// </summary>
public enum RecordPullRequestOutcome
{
    /// <summary>Recorded, or already known exactly as reported.</summary>
    Recorded = 0,

    /// <summary>The report carried no URL, so there was nothing to record.</summary>
    InvalidRequest = 1,

    /// <summary>No work item with that id.</summary>
    NotFound = 2,

    /// <summary>
    /// Other writers kept winning the race for the item's PR list. Nothing was
    /// recorded and nothing was lost; the same report will go through on a
    /// later attempt.
    /// </summary>
    Conflict = 3,
}

public sealed class RecordPullRequestRequest
{
    public string Url { get; set; } = string.Empty;

    /// <summary>The ILD loop run that opened the PR, when it came from one.</summary>
    public Guid? LoopRunId { get; set; }

    /// <summary>Whether the caller has observed the PR merged. Never un-merges an entry.</summary>
    public bool Merged { get; set; }

    /// <summary>
    /// When the PR entered the item's history — the start of the run that
    /// opened it, so history keeps the runs' order however late the client gets
    /// around to reporting it. Defaults to the server clock.
    /// </summary>
    public DateTime? CreatedAt { get; set; }
}

public sealed class AddDependencyRequest
{
    public string DependencyId { get; set; } = string.Empty;
}

public sealed class PollResponse
{
    public IReadOnlyList<WorkItemDto> ActiveItems { get; set; } = Array.Empty<WorkItemDto>();
    public IReadOnlyList<WorkItemDto> ReadyItems { get; set; } = Array.Empty<WorkItemDto>();
}

/// <summary>
/// The five editable fields of a work item. On a proposal's <c>Proposed</c>
/// side a null field was not proposed and a blank branch override clears it;
/// on its <c>Snapshot</c> side they are the item's values when it was proposed.
/// </summary>
public sealed class EditProposalFieldsDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
    public string? BranchNameOverride { get; set; }
    public string? BaseBranchOverride { get; set; }
}

public sealed class WorkItemEditProposalDto
{
    public Guid Id { get; set; }
    public string WorkItemId { get; set; } = string.Empty;
    public WorkItemEditProposalStatus Status { get; set; }
    public EditProposalFieldsDto Proposed { get; set; } = new();
    public EditProposalFieldsDto Snapshot { get; set; } = new();
    public string? Rationale { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public sealed class CreateEditProposalRequest
{
    /// <summary>Null = not proposed. Blank is refused: a work item always has a title.</summary>
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>Null = not proposed; blank = a proposal to clear it.</summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>Null = not proposed; blank = a proposal to clear it.</summary>
    public string? BaseBranchOverride { get; set; }

    public string? Rationale { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
}

public sealed class RejectEditProposalRequest
{
    public string? Reason { get; set; }
}

public sealed class MarkEditProposalsDeliveredRequest
{
    public IReadOnlyList<Guid> Ids { get; set; } = Array.Empty<Guid>();
}

/// <summary>
/// The answer to an approve or reject: what came of it, the proposal as it now
/// stands, and — for an applied approve — the updated work item.
/// </summary>
public sealed class EditProposalDecisionResponse
{
    public EditProposalDecisionOutcome Outcome { get; set; }
    public string? Error { get; set; }
    public WorkItemEditProposalDto? Proposal { get; set; }
    public WorkItemDto? WorkItem { get; set; }
}

public enum EditProposalCreateOutcome
{
    Created = 0,
    NotFound = 1,

    /// <summary>The request broke a rule; the result's error says which.</summary>
    Invalid = 2,

    TooManyPending = 3,
}

/// <summary>
/// What came of deciding a proposal. Stale and NotPending are both refusals,
/// but only Stale means the human's edit is why nothing was applied.
/// </summary>
public enum EditProposalDecisionOutcome
{
    Applied = 0,
    Rejected = 1,
    Stale = 2,
    NotPending = 3,
    NotFound = 4,
}
