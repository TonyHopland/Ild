namespace ILD.Core.Services.Remote;

/// <summary>
/// Mirrors the WorkItem status enum exposed by the WorkItem server's REST
/// surface. Kept as an independent type so ILD.Core does not have to take a
/// project reference on the server assembly.
/// </summary>
public enum RemoteWorkItemStatus
{
    Backlog = 0,
    WorkQueue = 1,
    Ready = 2,
    Running = 3,
    HumanFeedback = 4,
    WaitingForIld = 5,
    Done = 6,
}

public enum RemoteWorkItemPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// Mirrors the WorkItem server's AiProviderOverrideMode. Kept independent so
/// ILD.Core does not reference the server assembly (see <see cref="RemoteWorkItemStatus"/>).
/// <c>None</c> leaves each AI node's provider untouched; <c>OverrideDefault</c>
/// swaps only nodes that fell back to the configured default provider;
/// <c>OverrideAll</c> swaps every AI node regardless of what the loop pinned.
/// </summary>
public enum RemoteAiProviderOverrideMode
{
    None = 0,
    OverrideDefault = 1,
    OverrideAll = 2,
}

/// <summary>
/// One PR the server holds against a work item (mirrors the server's
/// WorkItemPullRequest). The server is the source of truth for these: a PR
/// touches the repository and belongs to the work item, unlike the worktree,
/// branch and PR snapshot, which are throwaway ILD-local run state.
/// </summary>
public sealed record RemoteWorkItemPullRequest(string Url, Guid? LoopRunId, bool Merged, DateTime CreatedAt);

/// <summary>
/// One file the WorkItem server holds against a work item — metadata only. The
/// bytes are fetched one attachment at a time, so a work item read never carries
/// them.
/// </summary>
public sealed record RemoteWorkItemAttachment(Guid Id, string FileName, string ContentType, long SizeBytes, DateTime CreatedAt);

/// <summary>One file on its way to the WorkItem server.</summary>
public sealed record RemoteAttachmentUpload(string FileName, string? ContentType, byte[] Content);

/// <summary>
/// What the WorkItem server made of an upload. A refusal is <em>not</em> an
/// outage: the per-work-item total is only knowable there, so its 400 has to
/// reach the user with the server's own message instead of collapsing into the
/// "WorkItemServer unreachable" every other failure of this client maps to.
/// </summary>
public enum AttachmentUploadOutcome
{
    Created = 0,

    /// <summary>No such work item.</summary>
    NotFound = 1,

    /// <summary>A limit was broken; <c>Error</c> says which.</summary>
    Rejected = 2,
}

public sealed record AttachmentUploadResult(
    AttachmentUploadOutcome Outcome,
    string? Error,
    IReadOnlyList<RemoteWorkItemAttachment> Created);

public sealed class RemoteWorkItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public RemoteWorkItemPriority Priority { get; set; }
    public RemoteWorkItemStatus Status { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();

    /// <summary>Every PR opened against this item, newest first.</summary>
    public IReadOnlyList<RemoteWorkItemPullRequest> PullRequests { get; set; } = Array.Empty<RemoteWorkItemPullRequest>();

    /// <summary>The item's attachments, metadata only — never the bytes.</summary>
    public IReadOnlyList<RemoteWorkItemAttachment> Attachments { get; set; } = Array.Empty<RemoteWorkItemAttachment>();
    public string? HumanFeedbackActions { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? RepositoryId { get; set; }
    public RemoteAiProviderOverrideMode AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }

    /// <summary>
    /// The branch every run of this work item checks out, used verbatim with no
    /// <c>-run-&lt;n&gt;</c> suffix. Null/blank means the generated per-run name.
    /// See ADR-0008.
    /// </summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>
    /// The ref every run of this work item branches from — the base it is reset
    /// to and rebased onto, and the branch its PR targets. Null/blank means the
    /// repository's default branch. See ADR-0008.
    /// </summary>
    public string? BaseBranchOverride { get; set; }

    /// <summary>How many edit proposals on this item still wait for a human. Zero on a poll.</summary>
    public int PendingEditProposalCount { get; set; }
}

public sealed class RemoteCreateWorkItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public RemoteWorkItemPriority Priority { get; set; } = RemoteWorkItemPriority.Medium;
    public IReadOnlyList<string>? Tags { get; set; }
    public IReadOnlyList<string>? Dependencies { get; set; }
    public RemoteWorkItemStatus? ForceStatus { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? RepositoryId { get; set; }

    /// <summary>Custom branch name for every run of the new item; blank/null = generated.</summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>Ref the new item's runs branch from; blank/null = the repository's default branch.</summary>
    public string? BaseBranchOverride { get; set; }
}

public sealed class RemoteUpdateWorkItemRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }

    /// <summary>
    /// Replaces the item's custom branch name. Null leaves it untouched; blank
    /// clears it — see the server's UpdateWorkItemRequest.
    /// </summary>
    public string? BranchNameOverride { get; set; }

    /// <summary>
    /// Replaces the ref the item's runs branch from, under the same
    /// null-unchanged / blank-clears convention as <see cref="BranchNameOverride"/>.
    /// </summary>
    public string? BaseBranchOverride { get; set; }

    /// <summary>Replaces the repository the item's next run works in; null leaves it untouched.</summary>
    public Guid? RepositoryId { get; set; }

    /// <summary>
    /// When supplied, replaces the work item's AI provider override. Mode and
    /// target travel as a unit — see the server's UpdateWorkItemRequest.
    /// </summary>
    public RemoteAiProviderOverrideMode? AiProviderOverride { get; set; }
    public Guid? AiProviderOverrideId { get; set; }
}

public sealed class RemoteTransitionRequest
{
    public RemoteWorkItemStatus TargetStatus { get; set; }
    public string? Actions { get; set; }
}

public sealed class RemoteTransitionResponse
{
    public bool Success { get; set; }
    public RemoteWorkItemStatus ActualStatus { get; set; }
    public string? Reason { get; set; }
}

public sealed class RemotePollResponse
{
    public IReadOnlyList<RemoteWorkItem> ActiveItems { get; set; } = Array.Empty<RemoteWorkItem>();
    public IReadOnlyList<RemoteWorkItem> ReadyItems { get; set; } = Array.Empty<RemoteWorkItem>();
}

/// <summary>Mirrors the WorkItem server's WorkItemEditProposalStatus.</summary>
public enum RemoteEditProposalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Stale = 3,
}

/// <summary>
/// The five editable fields of a work item, and its dependencies. On a
/// proposal's <see cref="RemoteWorkItemEditProposal.Proposed"/> side null means
/// "not proposed", a blank branch override means "clear it", and the
/// dependencies are the ones to add and to remove; on its
/// <see cref="RemoteWorkItemEditProposal.Snapshot"/> side they are the item's
/// values when the proposal was made, the dependency set only when the
/// proposal changes it.
/// </summary>
public sealed class RemoteEditProposalFields
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
    public string? BranchNameOverride { get; set; }
    public string? BaseBranchOverride { get; set; }

    /// <summary>Proposed side only.</summary>
    public IReadOnlyList<RemoteEditProposalDependency>? AddDependencies { get; set; }

    /// <summary>Proposed side only.</summary>
    public IReadOnlyList<RemoteEditProposalDependency>? RemoveDependencies { get; set; }

    /// <summary>Snapshot side only.</summary>
    public IReadOnlyList<string>? Dependencies { get; set; }
}

/// <summary>A work item a proposal names as a dependency. Title is null once that item no longer exists.</summary>
public sealed class RemoteEditProposalDependency
{
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
}

/// <summary>
/// A Work Item Edit Proposal: an agent's suggested edit to a work item, held on
/// the WorkItem server and applied only when a human approves it.
/// </summary>
public sealed class RemoteWorkItemEditProposal
{
    public Guid Id { get; set; }
    public string WorkItemId { get; set; } = string.Empty;
    public RemoteEditProposalStatus Status { get; set; }
    public RemoteEditProposalFields Proposed { get; set; } = new();
    public RemoteEditProposalFields Snapshot { get; set; } = new();
    public string? Rationale { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }

    /// <summary>The loop run's step that was running when it was proposed; places its card in the Action tab.</summary>
    public Guid? CreatedByRunNodeId { get; set; }

    /// <summary>The sequence of the chat reply its card follows.</summary>
    public int? ChatReplySequence { get; set; }

    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// The work item whose loop run made it, filled in by ILD from the run; the
    /// WorkItem server never sends it. Null for a chat's proposal and when the
    /// run no longer exists.
    /// </summary>
    public string? RequestedByWorkItemId { get; set; }
}

public sealed class RemoteCreateEditProposalRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
    public string? BranchNameOverride { get; set; }
    public string? BaseBranchOverride { get; set; }
    public IReadOnlyList<string>? AddDependencies { get; set; }
    public IReadOnlyList<string>? RemoveDependencies { get; set; }
    public string? Rationale { get; set; }
    public Guid? CreatedByLoopRunId { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }
    public Guid? CreatedByRunNodeId { get; set; }
    public int? ChatReplySequence { get; set; }
}

/// <summary>
/// What the WorkItem server made of a proposal. A refusal is not an outage: it
/// carries the server's reason back to the agent that proposed.
/// </summary>
public enum EditProposalCreateOutcome
{
    Created = 0,
    NotFound = 1,

    /// <summary>The proposal broke a rule; <c>Error</c> says which.</summary>
    Invalid = 2,

    /// <summary>The item already has as many pending proposals as it may.</summary>
    TooManyPending = 3,
}

public sealed record EditProposalCreateResult(
    EditProposalCreateOutcome Outcome,
    string? Error,
    RemoteWorkItemEditProposal? Proposal);

/// <summary>Mirrors the WorkItem server's outcome of an approve or reject.</summary>
public enum EditProposalDecisionOutcome
{
    Applied = 0,
    Rejected = 1,

    /// <summary>The item changed after the proposal was made; nothing was applied and the proposal is Stale.</summary>
    Stale = 2,

    /// <summary>The proposal had already been decided.</summary>
    NotPending = 3,

    /// <summary>No such proposal on that work item.</summary>
    NotFound = 4,

    /// <summary>
    /// ILD's own refusal, never sent by the WorkItem server: applying the
    /// proposal's dependency additions would close a cycle or name an item that
    /// no longer exists, so nothing was sent and the proposal stays Pending.
    /// </summary>
    Refused = 5,
}

/// <param name="Proposal">The proposal as it now stands; null only for <see cref="EditProposalDecisionOutcome.NotFound"/>.</param>
/// <param name="WorkItem">The updated item, for <see cref="EditProposalDecisionOutcome.Applied"/> only.</param>
public sealed record EditProposalDecisionResult(
    EditProposalDecisionOutcome Outcome,
    RemoteWorkItemEditProposal? Proposal,
    RemoteWorkItem? WorkItem);

public sealed class RemoteEditProposalQuery
{
    public RemoteEditProposalStatus? Status { get; set; }
    public Guid? CreatedByChatSessionId { get; set; }

    /// <summary>Only decided proposals whose decision the proposing chat has not been told.</summary>
    public bool UndeliveredOnly { get; set; }

    /// <summary>Only proposals made by one of these loop runs; null does not filter, and an empty list matches nothing.</summary>
    public IReadOnlyList<Guid>? CreatedByLoopRunIds { get; set; }
}
